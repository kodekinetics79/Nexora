-- ==========================================================================
-- Functions (142; 36 SECURITY DEFINER)
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
--
-- Every statement is IDEMPOTENT. Production is still at the pre-squash head with
-- the whole schema already materialised, and Program.cs applies migrations
-- uncaught at boot, so a bare CREATE here is a failed deploy. Objects with no
-- IF NOT EXISTS form are wrapped in a DO block that checks pg_catalog for that
-- exact object - never a broader condition that could skip a policy or a
-- constraint the database is genuinely missing.
-- ==========================================================================

--
-- Name: nexora_guard_accounting_outbox(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_accounting_outbox() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'accounting outbox records are immutable'; END IF;
    IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
       OR NEW."SubscriptionInvoiceId" IS DISTINCT FROM OLD."SubscriptionInvoiceId"
       OR NEW."SubscriptionRevenueActionId" IS DISTINCT FROM OLD."SubscriptionRevenueActionId"
       OR NEW."MessageType" IS DISTINCT FROM OLD."MessageType" OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
       OR NEW."PayloadJson" IS DISTINCT FROM OLD."PayloadJson" OR NEW."PayloadSha256" IS DISTINCT FROM OLD."PayloadSha256"
       OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc" OR NEW."MaxAttempts" IS DISTINCT FROM OLD."MaxAttempts"
    THEN RAISE EXCEPTION 'accounting outbox identity and payload are immutable'; END IF;
    IF NOT ((OLD."Status" IN ('Pending','RetryScheduled') AND NEW."Status"='InFlight')
        OR (OLD."Status"='InFlight' AND NEW."Status" IN ('InFlight','Acknowledged','RetryScheduled','Poison'))
        OR (OLD."Status"='Poison' AND NEW."Status"='Pending'))
    THEN RAISE EXCEPTION 'invalid accounting outbox transition % -> %',OLD."Status",NEW."Status"; END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_guard_append_only_record(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_append_only_record() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION
        'Table %.% is append-only: % is refused. This record is evidence about a '
        'tenant and must outlive the tenant it describes.',
        TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_guard_billing_statement_line_mutation(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_billing_statement_line_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE parent_status text;
BEGIN
    SELECT statement."Status" INTO parent_status
    FROM platform."BillingStatements" statement
    WHERE statement."Id" = COALESCE(NEW."BillingStatementId", OLD."BillingStatementId");

    IF parent_status = 'Final' THEN
        RAISE EXCEPTION 'Lines of a finalized billing statement are immutable'
            USING ERRCODE = '55000';
    END IF;

    RETURN COALESCE(NEW, OLD);
END
$$;


--
-- Name: nexora_guard_billing_statement_mutation(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_billing_statement_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD."Status" = 'Final' THEN
            RAISE EXCEPTION 'Finalized billing statements are immutable'
                USING ERRCODE = '55000';
        END IF;
        RETURN OLD;
    END IF;

    -- A Final row is closed for every further write. Draft -> Final is the
    -- last permitted transition and is applied while the row is still Draft.
    IF OLD."Status" = 'Final' THEN
        RAISE EXCEPTION 'Finalized billing statements are immutable'
            USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END
$$;


--
-- Name: nexora_guard_provisioning_lease_transfer(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_provisioning_lease_transfer() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Only a LIVE lease is protected. Timestamps in this schema are
    -- 'timestamp without time zone' holding UTC, so the comparison must be made
    -- against UTC and never against the server's local now().
    IF OLD."State" <> 'Running'
       OR OLD."LeaseToken" IS NULL
       OR OLD."LeaseUntil" IS NULL
       OR OLD."LeaseUntil" <= (now() AT TIME ZONE 'utc') THEN
        RETURN NEW;
    END IF;

    IF NEW."LeaseOwner" IS NOT DISTINCT FROM OLD."LeaseOwner"
       AND NEW."LeaseToken" IS NOT DISTINCT FROM OLD."LeaseToken"
       AND NEW."LeaseUntil" >= OLD."LeaseUntil" THEN
        -- Renewal, or any write that leaves ownership alone. This is the runner
        -- marking a step, which happens several times per execution.
        RETURN NEW;
    END IF;

    -- The holder standing down: all three lease columns released together, and the
    -- execution parked where nothing runs again without a fresh decision.
    IF NEW."LeaseOwner" IS NULL
       AND NEW."LeaseToken" IS NULL
       AND NEW."LeaseUntil" IS NULL
       AND NEW."State" IN ('Succeeded', 'Failed', 'Cancelled') THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION
        'Provisioning execution % is leased by % until % (UTC) and its ownership '
        'cannot be changed: a live lease means a runner is presumed to be mid-step, '
        'and a second runner on the same half-built tenant would write the same rows '
        'twice. Wait for the lease to lapse, then recover it through '
        'IProvisioningLeaseRecovery so the transfer carries evidence and an audit '
        'record.',
        OLD."Id", OLD."LeaseOwner", OLD."LeaseUntil"
        USING ERRCODE = '55006';
END
$$;


--
-- Name: nexora_guard_subscription_invoice(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_invoice() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Subscription invoices are immutable; use a governed revenue action'; END IF;
    IF OLD."Status"='Draft' AND NEW."Status" NOT IN ('Draft','Finalized') THEN RAISE EXCEPTION 'A draft subscription invoice may only be finalized'; END IF;
    IF OLD."Status"<>'Draft' AND NEW."Status"='Draft' THEN RAISE EXCEPTION 'A posted subscription invoice can never return to draft'; END IF;
    IF OLD."Status"='Void' AND NEW."Status"<>'Void' THEN RAISE EXCEPTION 'A void subscription invoice is terminal'; END IF;
    IF OLD."Status"='Draft' AND NEW."Status"='Finalized' AND NEW."TaxAmount">0 AND NEW."TaxRuleId" IS NULL
    THEN RAISE EXCEPTION 'A taxable invoice requires governed tax determination evidence'; END IF;
    IF OLD."Status"='Draft' AND NEW."Status"='Finalized' AND (
        NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId"
        OR NEW."Currency" IS DISTINCT FROM OLD."Currency" OR NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal"
        OR NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent" OR NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount"
        OR NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount" OR NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc"
        OR NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc" OR NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson"
        OR NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson" OR NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment"
        OR NEW."TaxJurisdictionCode" IS DISTINCT FROM OLD."TaxJurisdictionCode" OR NEW."TaxRuleId" IS DISTINCT FROM OLD."TaxRuleId"
        OR NEW."TaxRuleVersion" IS DISTINCT FROM OLD."TaxRuleVersion" OR NEW."TaxEvidenceJson" IS DISTINCT FROM OLD."TaxEvidenceJson"
        OR NEW."TaxEvidenceSha256" IS DISTINCT FROM OLD."TaxEvidenceSha256" OR NEW."TaxDeterminedAtUtc" IS DISTINCT FROM OLD."TaxDeterminedAtUtc"
        OR NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson" OR NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256"
        OR NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy" OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc")
    THEN RAISE EXCEPTION 'Invoice source and tax evidence cannot change during finalization'; END IF;
    IF OLD."Status"<>'Draft' AND (
        NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId"
        OR NEW."InvoiceNumber" IS DISTINCT FROM OLD."InvoiceNumber" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
        OR NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal" OR NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent"
        OR NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount" OR NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount"
        OR NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc" OR NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc"
        OR NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson" OR NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson"
        OR NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment" OR NEW."TaxJurisdictionCode" IS DISTINCT FROM OLD."TaxJurisdictionCode"
        OR NEW."TaxRuleId" IS DISTINCT FROM OLD."TaxRuleId" OR NEW."TaxRuleVersion" IS DISTINCT FROM OLD."TaxRuleVersion"
        OR NEW."TaxEvidenceJson" IS DISTINCT FROM OLD."TaxEvidenceJson" OR NEW."TaxEvidenceSha256" IS DISTINCT FROM OLD."TaxEvidenceSha256"
        OR NEW."TaxDeterminedAtUtc" IS DISTINCT FROM OLD."TaxDeterminedAtUtc"
        OR NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson" OR NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256"
        OR NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy" OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
        OR NEW."FinalizedBy" IS DISTINCT FROM OLD."FinalizedBy" OR NEW."FinalizedAtUtc" IS DISTINCT FROM OLD."FinalizedAtUtc")
    THEN RAISE EXCEPTION 'Finalized subscription invoice identity and evidence are immutable'; END IF;
    IF NEW."CreditedAmount"<OLD."CreditedAmount" OR NEW."PaidAmount"<OLD."PaidAmount"
       OR NEW."RefundedAmount"<OLD."RefundedAmount" OR NEW."ReversedPaymentAmount"<OLD."ReversedPaymentAmount"
       OR NEW."WrittenOffAmount"<OLD."WrittenOffAmount"
    THEN RAISE EXCEPTION 'Subscription revenue rollups are monotonic append-only totals'; END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_guard_subscription_revenue_action(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_revenue_action() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'subscription revenue actions are append-only'; END IF;
    IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
       OR NEW."SubscriptionInvoiceId" IS DISTINCT FROM OLD."SubscriptionInvoiceId"
       OR NEW."Kind" IS DISTINCT FROM OLD."Kind" OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
       OR NEW."Amount" IS DISTINCT FROM OLD."Amount" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
       OR NEW."Reason" IS DISTINCT FROM OLD."Reason" OR NEW."EvidenceSha256" IS DISTINCT FROM OLD."EvidenceSha256"
       OR NEW."ExternalReference" IS DISTINCT FROM OLD."ExternalReference"
       OR NEW."ProposedByPlatformUserId" IS DISTINCT FROM OLD."ProposedByPlatformUserId"
       OR NEW."ProposedAtUtc" IS DISTINCT FROM OLD."ProposedAtUtc"
    THEN RAISE EXCEPTION 'subscription revenue action identity and evidence are immutable'; END IF;
    IF NOT ((OLD."Status"='Proposed' AND NEW."Status" IN ('Completed','Failed'))
         OR (OLD."Status"='Approved' AND NEW."Status" IN ('Completed','Failed')))
    THEN RAISE EXCEPTION 'invalid subscription revenue action transition % -> %',OLD."Status",NEW."Status"; END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_guard_subscription_tax_rule(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_tax_rule() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP='DELETE' THEN RAISE EXCEPTION 'subscription tax rules are immutable'; END IF;
    IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."JurisdictionCode" IS DISTINCT FROM OLD."JurisdictionCode"
       OR NEW."BuyerCountryCode" IS DISTINCT FROM OLD."BuyerCountryCode" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
       OR NEW."Treatment" IS DISTINCT FROM OLD."Treatment" OR NEW."RatePercent" IS DISTINCT FROM OLD."RatePercent"
       OR NEW."LegalAuthorityReference" IS DISTINCT FROM OLD."LegalAuthorityReference"
       OR NEW."EvidenceSha256" IS DISTINCT FROM OLD."EvidenceSha256" OR NEW."EffectiveFromUtc" IS DISTINCT FROM OLD."EffectiveFromUtc"
       OR NEW."EffectiveToUtc" IS DISTINCT FROM OLD."EffectiveToUtc" OR NEW."Version" IS DISTINCT FROM OLD."Version"
       OR NEW."ProposedByPlatformUserId" IS DISTINCT FROM OLD."ProposedByPlatformUserId"
       OR NEW."ProposedAtUtc" IS DISTINCT FROM OLD."ProposedAtUtc"
    THEN RAISE EXCEPTION 'subscription tax rule legal evidence is immutable'; END IF;
    IF NOT ((OLD."Status"='Draft' AND NEW."Status"='Approved') OR (OLD."Status"='Approved' AND NEW."Status"='Retired'))
    THEN RAISE EXCEPTION 'invalid subscription tax rule transition % -> %',OLD."Status",NEW."Status"; END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_guard_tenant_legal_hold(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_tenant_legal_hold() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Tenant legal holds are immutable and cannot be deleted.'
            USING ERRCODE = '55000';
    END IF;
    IF OLD."TenantId" IS DISTINCT FROM NEW."TenantId"
       OR OLD."Scope" IS DISTINCT FROM NEW."Scope"
       OR OLD."Authority" IS DISTINCT FROM NEW."Authority"
       OR OLD."Reason" IS DISTINCT FROM NEW."Reason"
       OR OLD."EvidenceReference" IS DISTINCT FROM NEW."EvidenceReference"
       OR OLD."PlacedOn" IS DISTINCT FROM NEW."PlacedOn"
       OR OLD."PlacedByPlatformUserId" IS DISTINCT FROM NEW."PlacedByPlatformUserId"
       OR OLD."PlacedBy" IS DISTINCT FROM NEW."PlacedBy" THEN
        RAISE EXCEPTION 'Tenant legal-hold placement evidence is immutable.'
            USING ERRCODE = '55000';
    END IF;
    IF OLD."ReleasedOn" IS NOT NULL AND (
       OLD."ReleasedOn" IS DISTINCT FROM NEW."ReleasedOn"
       OR OLD."ReleasedByPlatformUserId" IS DISTINCT FROM NEW."ReleasedByPlatformUserId"
       OR OLD."ReleasedBy" IS DISTINCT FROM NEW."ReleasedBy"
       OR OLD."ReleaseReason" IS DISTINCT FROM NEW."ReleaseReason") THEN
        RAISE EXCEPTION 'A released tenant legal hold cannot be rewritten.'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_guard_usage_event_insert(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_guard_usage_event_insert() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    original platform."UsageEvents"%ROWTYPE;
    prior_quantity numeric; prior_cost numeric; prior_rated numeric;
    card platform."RateCards"%ROWTYPE; line platform."RateCardLines"%ROWTYPE;
    expected_meter text; priced_divisor numeric;
BEGIN
    IF NEW."Kind"='Consumption' THEN
        IF NEW."OverageQuantity"<>GREATEST(NEW."Quantity"-NEW."AllowanceApplied",0) THEN
            RAISE EXCEPTION 'usage overage does not reconcile';
        END IF;
    ELSE
        SELECT * INTO original FROM platform."UsageEvents"
         WHERE "TenantId"=NEW."TenantId" AND "UsageEventId"=NEW."AdjustsUsageEventId" FOR KEY SHARE;
        IF NOT FOUND OR original."Kind"<>'Consumption' THEN RAISE EXCEPTION 'adjustment must reference same-tenant consumption'; END IF;
        IF NEW."EventType"<>original."EventType" OR NEW."Unit"<>original."Unit" OR NEW."Currency"<>original."Currency"
           OR NEW."RateCardId" IS DISTINCT FROM original."RateCardId" OR NEW."RateCardLineId" IS DISTINCT FROM original."RateCardLineId"
           OR NEW."RateCardVersion" IS DISTINCT FROM original."RateCardVersion" OR NEW."UnitPrice" IS DISTINCT FROM original."UnitPrice"
           OR NEW."RatingStatus"<>original."RatingStatus" OR NEW."AllowanceApplied"<>0 OR NEW."OverageQuantity"<>NEW."Quantity" THEN
            RAISE EXCEPTION 'adjustment lineage does not match original usage';
        END IF;
        SELECT COALESCE(SUM("Quantity"),0),COALESCE(SUM("CostAmount"),0),COALESCE(SUM("RatedAmount"),0)
          INTO prior_quantity,prior_cost,prior_rated FROM platform."UsageEvents"
         WHERE "TenantId"=NEW."TenantId" AND "AdjustsUsageEventId"=NEW."AdjustsUsageEventId";
        IF original."Quantity"+prior_quantity+NEW."Quantity"<0 OR original."CostAmount"+prior_cost+NEW."CostAmount"<0
           OR (original."RatedAmount" IS NOT NULL AND original."RatedAmount"+prior_rated+COALESCE(NEW."RatedAmount",0)<0) THEN
            RAISE EXCEPTION 'cumulative adjustment exceeds original usage';
        END IF;
    END IF;
    IF NEW."RatingStatus"='Rated' THEN
        IF NEW."RateCardId" IS NULL OR NEW."RateCardLineId" IS NULL OR NEW."RateCardVersion" IS NULL OR NEW."UnitPrice" IS NULL THEN
            RAISE EXCEPTION 'rated usage requires complete rate-card lineage';
        END IF;
        SELECT * INTO card FROM platform."RateCards" WHERE "Id"=NEW."RateCardId";
        SELECT * INTO line FROM platform."RateCardLines" WHERE "Id"=NEW."RateCardLineId";
        expected_meter:=CASE NEW."EventType" WHEN 'ai.tokens' THEN 'ai.tokens.external' WHEN 'storage.gb-hours' THEN 'storage.gb' ELSE NEW."EventType" END;
        priced_divisor:=CASE NEW."EventType" WHEN 'ai.tokens' THEN 1000 WHEN 'storage.gb-hours' THEN 1073741824 ELSE 1 END;
        IF card."Id" IS NULL OR line."Id" IS NULL OR line."RateCardId"<>card."Id" OR line."MeterKey"<>expected_meter
           OR card."Version"<>NEW."RateCardVersion" OR card."Currency"<>NEW."Currency" OR NOT card."IsActive"
           OR card."EffectiveFromUtc">(NEW."OccurredAtUtc" AT TIME ZONE 'UTC')
           OR (card."EffectiveToUtc" IS NOT NULL AND card."EffectiveToUtc"<=(NEW."OccurredAtUtc" AT TIME ZONE 'UTC'))
           OR line."UnitPrice"<>NEW."UnitPrice" OR NEW."AllowanceApplied">line."IncludedQuantity"
           OR NEW."RatedAmount" IS DISTINCT FROM ROUND(NEW."OverageQuantity"*NEW."UnitPrice"/priced_divisor,6) THEN
            RAISE EXCEPTION 'rated usage does not match the effective rate-card line';
        END IF;
    ELSIF NEW."RatedAmount" IS NOT NULL THEN
        RAISE EXCEPTION 'unrated usage cannot carry a rated amount';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_reconcile_subscription_invoice_rollups(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    invoice_id bigint;
    invoice_row platform."SubscriptionInvoices"%ROWTYPE;
    credited numeric(14,2);
    paid numeric(14,2);
    refunded numeric(14,2);
    reversed numeric(14,2);
    written_off numeric(14,2);
BEGIN
    IF TG_RELID='platform."SubscriptionInvoices"'::regclass THEN
        invoice_id := NEW."Id";
    ELSE
        invoice_id := NEW."SubscriptionInvoiceId";
    END IF;
    SELECT * INTO invoice_row FROM platform."SubscriptionInvoices" WHERE "Id"=invoice_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT COALESCE(sum("Amount"),0) INTO credited FROM platform."SubscriptionCreditNotes" WHERE "SubscriptionInvoiceId"=invoice_id;
    SELECT COALESCE(sum("Amount"),0) INTO paid FROM platform."SubscriptionPayments" WHERE "SubscriptionInvoiceId"=invoice_id;
    SELECT COALESCE(sum("Amount") FILTER (WHERE "Kind"='Refund' AND "Status"='Completed'),0),
           COALESCE(sum("Amount") FILTER (WHERE "Kind"='PaymentReversal' AND "Status"='Completed'),0),
           COALESCE(sum("Amount") FILTER (WHERE "Kind"='WriteOff' AND "Status"='Completed'),0)
      INTO refunded,reversed,written_off
      FROM platform."SubscriptionRevenueActions" WHERE "SubscriptionInvoiceId"=invoice_id;
    IF invoice_row."CreditedAmount"<>credited OR invoice_row."PaidAmount"<>paid
       OR invoice_row."RefundedAmount"<>refunded OR invoice_row."ReversedPaymentAmount"<>reversed
       OR invoice_row."WrittenOffAmount"<>written_off
    THEN RAISE EXCEPTION 'subscription invoice rollups do not reconcile to append-only revenue records'; END IF;
    RETURN NULL;
END $$;


--
-- Name: nexora_seed_tenant_meter_source_policies(); Type: FUNCTION; Schema: platform; Owner: -
--

CREATE OR REPLACE FUNCTION platform.nexora_seed_tenant_meter_source_policies() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'platform'
    AS $$
BEGIN
    INSERT INTO platform."TenantMeterSourcePolicies" ("TenantId","MeterKey","Mode","Version")
    SELECT NEW."Id",m.meter_key,m.mode,1
      FROM (VALUES
        ('base.subscription','LegacyAuthoritative'),('documents','LegacyAuthoritative'),
        ('ai.tokens.external','LegacyAuthoritative'),('seats','LegacyAuthoritative'),
        ('processing.minutes','BillingBlocked'),('pages.processed','BillingBlocked'),
        ('rfqs','BillingBlocked'),('quotes','BillingBlocked'),('orders','BillingBlocked'),
        ('emails','BillingBlocked'),('pages.ocr','BillingBlocked'),('api.calls','BillingBlocked'),
        ('storage.gb','BillingBlocked'),('supplier.searches','BillingBlocked'),
        ('automation.runs','BillingBlocked'),('dedicated.infrastructure','BillingBlocked')) AS m(meter_key,mode)
    ON CONFLICT ("TenantId","MeterKey") DO NOTHING;
    RETURN NEW;
END $$;


--
-- Name: nexora_ai_policy_audit_allowed(bigint, text, text, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ai_policy_audit_allowed(tenant_id bigint, action_name text, target_type text, target_id text) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public', 'platform'
    AS $$
    SELECT action_name = 'tenant.ai-policy.update'
       AND target_type = 'AiProcessingPolicy'
       AND target_id = NULLIF(current_setting('nexora.business_unit_id', true), '')
       AND EXISTS (
           SELECT 1 FROM platform."Tenants" tenant
           WHERE tenant."Id" = tenant_id
             AND tenant."PrimaryBusinessUnitId" =
                 NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
$$;


--
-- Name: nexora_ar_evidence_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ar_evidence_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE row_data jsonb := to_jsonb(NEW);
DECLARE prior_data jsonb := CASE WHEN TG_OP = 'UPDATE' THEN to_jsonb(OLD) ELSE '{}'::jsonb END;
DECLARE aggregate_type text;
DECLARE aggregate_version bigint;
DECLARE event_action text;
DECLARE event_actor text;
DECLARE event_time timestamp without time zone := now();
BEGIN
    IF TG_TABLE_NAME = 'DunningNotices' AND TG_OP = 'UPDATE'
       AND row_data->>'Status' IN ('Delivered','Failed') THEN
        IF NOT EXISTS (
            SELECT 1 FROM public."DunningDeliveryAttempts" attempt
            WHERE attempt."BusinessUnitId" = NEW."BusinessUnitId"
              AND attempt."DunningNoticeId" = NEW."Id"
              AND attempt."Status" = row_data->>'Status'
              AND attempt."ProviderReference" = row_data->>'ProviderReference'
              AND attempt."ArtifactHash" = row_data->>'ArtifactHash'
              AND attempt."TemplateVersion" = row_data->>'TemplateVersion'
              AND attempt."FailureCode" IS NOT DISTINCT FROM row_data->>'FailureCode'
              AND attempt."OccurredOn" >= (row_data->>'ReleasedOn')::timestamp
              AND attempt."ProviderOccurredOn" >= (row_data->>'ReleasedOn')::timestamp) THEN
            RAISE EXCEPTION 'a terminal notice requires matching immutable delivery evidence'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    aggregate_type := CASE TG_TABLE_NAME
        WHEN 'FinanceCommunicationContacts' THEN 'FinanceCommunicationContact'
        WHEN 'CustomerStatements' THEN 'CustomerStatement'
        WHEN 'CustomerStatementLines' THEN 'CustomerStatementLine'
        WHEN 'DunningPolicies' THEN 'DunningPolicy'
        WHEN 'DunningPolicySteps' THEN 'DunningPolicyStep'
        WHEN 'CustomerCollectionProfiles' THEN 'CustomerCollectionProfile'
        WHEN 'CollectionControls' THEN 'CollectionControl'
        WHEN 'DunningCases' THEN 'DunningCase'
        WHEN 'PromisesToPay' THEN 'PromiseToPay'
        WHEN 'DunningRuns' THEN 'DunningRun'
        WHEN 'DunningRunDecisions' THEN 'DunningRunDecision'
        WHEN 'DunningNotices' THEN 'DunningNotice'
        ELSE 'DunningDeliveryAttempt' END;
    aggregate_version := COALESCE(NULLIF(row_data->>'Version','')::bigint, 1);
    event_action := CASE WHEN TG_OP = 'INSERT' THEN 'Created'
        ELSE COALESCE(row_data->>'Status', 'Updated') END;
    IF TG_TABLE_NAME = 'DunningRunDecisions' THEN
        SELECT r."CreatedBy" INTO event_actor FROM public."DunningRuns" r
         WHERE r."BusinessUnitId" = NEW."BusinessUnitId" AND r."Id" = NEW."DunningRunId";
    ELSIF TG_OP = 'INSERT' THEN
        event_actor := COALESCE(row_data->>'RecordedBy', row_data->>'CreatedBy', 'database');
    ELSE
        event_actor := CASE TG_TABLE_NAME
            WHEN 'FinanceCommunicationContacts' THEN row_data->>'DeactivatedBy'
            WHEN 'CustomerStatements' THEN CASE row_data->>'Status'
                WHEN 'Finalized' THEN row_data->>'FinalizedBy'
                WHEN 'Cancelled' THEN row_data->>'CancelledBy'
                WHEN 'Superseded' THEN (
                    SELECT successor."FinalizedBy" FROM public."CustomerStatements" successor
                    WHERE successor."BusinessUnitId" = NEW."BusinessUnitId"
                      AND successor."SupersedesStatementId" = NEW."Id"
                      AND successor."Status" = 'Finalized'
                    ORDER BY successor."Revision" DESC LIMIT 1)
                ELSE NULL END
            WHEN 'DunningPolicies' THEN CASE row_data->>'Status'
                WHEN 'Approved' THEN row_data->>'ApprovedBy'
                WHEN 'Active' THEN row_data->>'ActivatedBy'
                WHEN 'Retired' THEN row_data->>'RetiredBy' ELSE NULL END
            WHEN 'CustomerCollectionProfiles' THEN row_data->>'ModifiedBy'
            WHEN 'CollectionControls' THEN row_data->>'ResolvedBy'
            WHEN 'DunningCases' THEN row_data->>'UpdatedBy'
            WHEN 'PromisesToPay' THEN row_data->>'ClosedBy'
            WHEN 'DunningRuns' THEN CASE row_data->>'Status'
                WHEN 'Running' THEN row_data->>'LeaseOwner'
                ELSE COALESCE(prior_data->>'LeaseOwner', row_data->>'CreatedBy') END
            WHEN 'DunningNotices' THEN CASE row_data->>'Status'
                WHEN 'Approved' THEN row_data->>'ApprovedBy'
                WHEN 'Released' THEN row_data->>'ReleasedBy'
                WHEN 'Delivered' THEN row_data->>'DeliveryUpdatedBy'
                WHEN 'Failed' THEN row_data->>'DeliveryUpdatedBy'
                WHEN 'Cancelled' THEN row_data->>'CancelledBy' ELSE NULL END
            ELSE COALESCE(row_data->>'RecordedBy', row_data->>'CreatedBy') END;
    END IF;
    event_actor := COALESCE(event_actor, 'database');
    INSERT INTO public."CommercialFinanceAudits"
        ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
    VALUES (NEW."BusinessUnitId", aggregate_type, NEW."Id", event_action, event_actor,
        event_time, jsonb_build_object('id', NEW."Id", 'status', row_data->>'Status',
            'version', aggregate_version, 'evidenceFingerprint', md5(row_data::text)));
    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", aggregate_type,
        NEW."Id", aggregate_version, 'finance.receivables.' || lower(TG_TABLE_NAME) || '.' || lower(event_action),
        jsonb_build_object('Id', NEW."Id", 'Status', row_data->>'Status',
            'Version', aggregate_version, 'Actor', event_actor), event_time);
    RETURN NEW;
END
$$;


--
-- Name: nexora_ar_governed_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ar_governed_mutation() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE old_data jsonb;
DECLARE new_data jsonb;
DECLARE parent_status text;
DECLARE payment_amount numeric;
DECLARE requires_approval boolean;
DECLARE trusted_actor text;
DECLARE actor_signature text;
DECLARE actor_secret text;
DECLARE expected_actor text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'governed receivables records cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'INSERT' THEN
        new_data := to_jsonb(NEW);
        IF current_setting('role', true) = 'nexora_tenant_app' THEN
            trusted_actor := NULLIF(current_setting('nexora.actor_id', true), '');
            actor_signature := NULLIF(current_setting('nexora.actor_signature', true), '');
            SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
            IF trusted_actor IS NULL OR actor_secret IS NULL OR actor_signature IS NULL
               OR actor_signature <> encode(hmac(convert_to(NEW."BusinessUnitId"::text || E'\n' || trusted_actor, 'UTF8'),
                    convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex') THEN
                RAISE EXCEPTION 'a signed authenticated transaction actor is required' USING ERRCODE = '42501';
            END IF;
            expected_actor := CASE TG_TABLE_NAME
                WHEN 'DunningDeliveryAttempts' THEN new_data->>'RecordedBy'
                WHEN 'CustomerStatementLines' THEN NULL
                WHEN 'DunningPolicySteps' THEN NULL
                WHEN 'DunningRunDecisions' THEN NULL
                ELSE new_data->>'CreatedBy' END;
            IF expected_actor IS NOT NULL AND expected_actor <> trusted_actor THEN
                RAISE EXCEPTION 'the mutation actor does not match the authenticated transaction actor' USING ERRCODE = '42501';
            END IF;
        END IF;
        IF new_data ? 'Version' AND (new_data->>'Version')::bigint <> 1 THEN
            RAISE EXCEPTION 'governed aggregates must begin at version one' USING ERRCODE = '55000';
        END IF;
        IF TG_TABLE_NAME = 'FinanceCommunicationContacts' THEN
            IF NOT NEW."IsActive" OR NOT NEW."IsVerified" OR NEW."DeactivatedBy" IS NOT NULL OR NEW."DeactivatedOn" IS NOT NULL THEN
                RAISE EXCEPTION 'communication contacts must begin active and verified' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'CustomerStatements' THEN
            IF NEW."Status" <> 'Draft' OR NEW."StatementNumber" IS NOT NULL OR NEW."FinalizedBy" IS NOT NULL
               OR NEW."FinalizedOn" IS NOT NULL OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL THEN
                RAISE EXCEPTION 'customer statements must begin as unnumbered drafts' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'CustomerStatementLines' THEN
            SELECT s."Status" INTO parent_status FROM public."CustomerStatements" s
             WHERE s."BusinessUnitId" = NEW."BusinessUnitId" AND s."Id" = NEW."CustomerStatementId" FOR UPDATE;
            IF parent_status <> 'Draft' THEN
                RAISE EXCEPTION 'statement lines can only be added to a draft' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'DunningPolicies' THEN
            IF NEW."Status" <> 'Draft' OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
               OR NEW."ActivatedBy" IS NOT NULL OR NEW."ActivatedOn" IS NOT NULL
               OR NEW."RetiredBy" IS NOT NULL OR NEW."RetiredOn" IS NOT NULL THEN
                RAISE EXCEPTION 'dunning policies must begin as unapproved drafts' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'DunningPolicySteps' THEN
            SELECT p."Status" INTO parent_status FROM public."DunningPolicies" p
             WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Id" = NEW."DunningPolicyId" FOR UPDATE;
            IF parent_status <> 'Draft' THEN
                RAISE EXCEPTION 'policy steps can only be added to a draft' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'CustomerCollectionProfiles' THEN
            IF NEW."ModifiedBy" IS NOT NULL OR NEW."ModifiedOn" IS NOT NULL THEN
                RAISE EXCEPTION 'collection profiles cannot begin with modification evidence' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'CollectionControls' THEN
            IF NEW."Status" <> 'Active' OR NEW."ResolvedBy" IS NOT NULL OR NEW."ResolvedOn" IS NOT NULL THEN
                RAISE EXCEPTION 'collection controls must begin active and unresolved' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'DunningCases' THEN
            IF NEW."Status" <> 'Open' OR NEW."CurrentStage" <> 0 OR NEW."UpdatedBy" IS NOT NULL
               OR NEW."UpdatedOn" IS NOT NULL OR NEW."PromiseAmount" IS NOT NULL OR NEW."PromiseDueOn" IS NOT NULL THEN
                RAISE EXCEPTION 'dunning cases must begin open at stage zero' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'PromisesToPay' THEN
            IF NEW."Status" <> 'Open' OR NEW."ClosedBy" IS NOT NULL OR NEW."ClosedOn" IS NOT NULL
               OR NEW."MatchedPaymentId" IS NOT NULL OR NEW."MatchedAmount" IS NOT NULL THEN
                RAISE EXCEPTION 'promises must begin open without settlement evidence' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'DunningRuns' THEN
            IF NEW."Status" <> 'Pending' OR NEW."LeaseOwner" IS NOT NULL OR NEW."LeaseToken" IS NOT NULL
               OR NEW."LeaseUntil" IS NOT NULL OR NEW."CompletedOn" IS NOT NULL
               OR NEW."CompletionEvidenceReference" IS NOT NULL OR NEW."FailureReason" IS NOT NULL
               OR NEW."FailureEvidenceReference" IS NOT NULL THEN
                RAISE EXCEPTION 'dunning runs must begin pending' USING ERRCODE = '55000';
            END IF;
        ELSIF TG_TABLE_NAME = 'DunningNotices' THEN
            IF NEW."Status" NOT IN ('Draft','Suppressed') OR NEW."ApprovedBy" IS NOT NULL
               OR NEW."ReleasedBy" IS NOT NULL OR NEW."DeliveryUpdatedBy" IS NOT NULL OR NEW."CancelledBy" IS NOT NULL THEN
                RAISE EXCEPTION 'dunning notices must begin draft or suppressed without terminal actors' USING ERRCODE = '55000';
            END IF;
        END IF;
        RETURN NEW;
    END IF;
    old_data := to_jsonb(OLD); new_data := to_jsonb(NEW);
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" THEN
        RAISE EXCEPTION 'business unit ownership is immutable' USING ERRCODE = '55000';
    END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        trusted_actor := NULLIF(current_setting('nexora.actor_id', true), '');
        actor_signature := NULLIF(current_setting('nexora.actor_signature', true), '');
        SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
        IF trusted_actor IS NULL OR actor_secret IS NULL OR actor_signature IS NULL
           OR actor_signature <> encode(hmac(convert_to(NEW."BusinessUnitId"::text || E'\n' || trusted_actor, 'UTF8'),
                convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex') THEN
            RAISE EXCEPTION 'a signed authenticated transaction actor is required' USING ERRCODE = '42501';
        END IF;
        expected_actor := CASE TG_TABLE_NAME
            WHEN 'FinanceCommunicationContacts' THEN new_data->>'DeactivatedBy'
            WHEN 'CustomerStatements' THEN CASE new_data->>'Status'
                WHEN 'Finalized' THEN new_data->>'FinalizedBy'
                WHEN 'Cancelled' THEN new_data->>'CancelledBy' ELSE NULL END
            WHEN 'DunningPolicies' THEN CASE new_data->>'Status'
                WHEN 'Approved' THEN new_data->>'ApprovedBy'
                WHEN 'Active' THEN new_data->>'ActivatedBy'
                WHEN 'Retired' THEN new_data->>'RetiredBy' ELSE NULL END
            WHEN 'CustomerCollectionProfiles' THEN new_data->>'ModifiedBy'
            WHEN 'CollectionControls' THEN new_data->>'ResolvedBy'
            WHEN 'DunningCases' THEN new_data->>'UpdatedBy'
            WHEN 'PromisesToPay' THEN new_data->>'ClosedBy'
            WHEN 'DunningRuns' THEN CASE new_data->>'Status'
                WHEN 'Running' THEN new_data->>'LeaseOwner' ELSE old_data->>'LeaseOwner' END
            WHEN 'DunningNotices' THEN CASE new_data->>'Status'
                WHEN 'Approved' THEN new_data->>'ApprovedBy'
                WHEN 'Released' THEN new_data->>'ReleasedBy'
                WHEN 'Delivered' THEN new_data->>'DeliveryUpdatedBy'
                WHEN 'Failed' THEN new_data->>'DeliveryUpdatedBy'
                WHEN 'Cancelled' THEN new_data->>'CancelledBy' ELSE NULL END
            ELSE NULL END;
        IF expected_actor IS NOT NULL AND expected_actor <> trusted_actor THEN
            RAISE EXCEPTION 'the mutation actor does not match the authenticated transaction actor' USING ERRCODE = '42501';
        END IF;
    END IF;
    IF old_data ? 'Version' AND (new_data->>'Version')::bigint <> (old_data->>'Version')::bigint + 1 THEN
        RAISE EXCEPTION 'aggregate version must advance exactly once' USING ERRCODE = '40001';
    END IF;
    IF TG_TABLE_NAME = 'FinanceCommunicationContacts' THEN
        IF OLD."IsActive" AND NOT NEW."IsActive" THEN
            IF (new_data - ARRAY['IsActive','EffectiveTo','DeactivatedBy','DeactivatedOn','DeactivationReason','Version'])
               IS DISTINCT FROM (old_data - ARRAY['IsActive','EffectiveTo','DeactivatedBy','DeactivatedOn','DeactivationReason','Version']) THEN
                RAISE EXCEPTION 'communication contact identity and verification evidence are immutable' USING ERRCODE = '55000';
            END IF;
        ELSE
            RAISE EXCEPTION 'invalid or immutable communication contact transition' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'CustomerStatements' THEN
        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Finalized' THEN
            IF (new_data - ARRAY['Status','StatementNumber','FinalizedBy','FinalizedOn','ArtifactReference','ArtifactContent','ArtifactHash','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','StatementNumber','FinalizedBy','FinalizedOn','ArtifactReference','ArtifactContent','ArtifactHash','Version']) THEN
                RAISE EXCEPTION 'statement snapshot changed during finalization' USING ERRCODE = '55000';
            END IF;
            IF position('{{STATEMENT_NUMBER}}' in OLD."ArtifactContent") = 0
               OR position('{{STATEMENT_NUMBER}}' in NEW."ArtifactContent") > 0
               OR NEW."ArtifactContent" <> replace(OLD."ArtifactContent", '{{STATEMENT_NUMBER}}', NEW."StatementNumber")
               OR NEW."ArtifactHash" = OLD."ArtifactHash"
               OR NEW."ArtifactHash" <> encode(digest(convert_to(NEW."ArtifactContent", 'UTF8'), 'sha256'), 'hex')
               OR NEW."FinalizedBy" IS NULL OR NEW."FinalizedBy" = OLD."CreatedBy" THEN
                RAISE EXCEPTION 'the finalized statement artifact is not the governed numbered rendering' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
            IF (new_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','Version']) THEN
                RAISE EXCEPTION 'statement snapshot changed during cancellation' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Finalized' AND NEW."Status" = 'Superseded' THEN
            IF (new_data - ARRAY['Status','Version']) IS DISTINCT FROM (old_data - ARRAY['Status','Version']) THEN
                RAISE EXCEPTION 'superseded statements are immutable' USING ERRCODE = '55000';
            END IF;
        ELSE
            RAISE EXCEPTION 'invalid or immutable statement transition' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'CustomerStatementLines' THEN
        RAISE EXCEPTION 'statement snapshot lines are append-only' USING ERRCODE = '55000';
    ELSIF TG_TABLE_NAME = 'DunningPolicySteps' THEN
        RAISE EXCEPTION 'dunning policy steps are append-only' USING ERRCODE = '55000';
    ELSIF TG_TABLE_NAME = 'DunningDeliveryAttempts' THEN
        RAISE EXCEPTION 'delivery evidence is append-only' USING ERRCODE = '55000';
    ELSIF TG_TABLE_NAME = 'DunningRunDecisions' THEN
        RAISE EXCEPTION 'dunning run decisions are append-only' USING ERRCODE = '55000';
    ELSIF TG_TABLE_NAME = 'DunningPolicies' THEN
        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
            IF (new_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version']) IS DISTINCT FROM
               (old_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version']) THEN
                RAISE EXCEPTION 'policy content changed during approval' USING ERRCODE = '55000';
            END IF;
            IF NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy" = OLD."CreatedBy" THEN
                RAISE EXCEPTION 'policy approval requires an independent checker' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Approved' AND NEW."Status" = 'Active' THEN
            IF (new_data - ARRAY['Status','ActivatedBy','ActivatedOn','Version']) IS DISTINCT FROM
               (old_data - ARRAY['Status','ActivatedBy','ActivatedOn','Version']) THEN
                RAISE EXCEPTION 'approved policy content is immutable' USING ERRCODE = '55000';
            END IF;
            IF NEW."ActivatedBy" IS NULL OR NEW."ActivatedBy" IN (OLD."CreatedBy", OLD."ApprovedBy") THEN
                RAISE EXCEPTION 'policy activation requires an independent operator' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Active' AND NEW."Status" = 'Retired' THEN
            IF (new_data - ARRAY['Status','RetiredBy','RetiredOn','Version']) IS DISTINCT FROM
               (old_data - ARRAY['Status','RetiredBy','RetiredOn','Version']) THEN
                RAISE EXCEPTION 'active policy content is immutable' USING ERRCODE = '55000';
            END IF;
        ELSE
            RAISE EXCEPTION 'invalid or immutable dunning policy transition' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'CustomerCollectionProfiles' THEN
        IF (new_data - ARRAY['DunningPolicyId','FinanceCommunicationContactId','Locale','TimeZoneId','Collector',
                'AutomaticDeliveryAllowed','IsOnHold','HoldReason','HoldEvidenceReference','ModifiedBy','ModifiedOn','Version'])
           IS DISTINCT FROM
           (old_data - ARRAY['DunningPolicyId','FinanceCommunicationContactId','Locale','TimeZoneId','Collector',
                'AutomaticDeliveryAllowed','IsOnHold','HoldReason','HoldEvidenceReference','ModifiedBy','ModifiedOn','Version'])
           OR NEW."ModifiedBy" IS NULL OR NEW."ModifiedOn" IS NULL THEN
            RAISE EXCEPTION 'invalid collection profile update' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'CollectionControls' THEN
        IF OLD."Status" <> 'Active' OR NEW."Status" <> 'Resolved'
           OR (new_data - ARRAY['Status','ResolvedBy','ResolvedOn','ResolutionReason','ResolutionEvidenceReference','Version'])
              IS DISTINCT FROM
              (old_data - ARRAY['Status','ResolvedBy','ResolvedOn','ResolutionReason','ResolutionEvidenceReference','Version']) THEN
            RAISE EXCEPTION 'invalid or immutable collection control transition' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'DunningCases' THEN
        IF OLD."Status" IN ('Resolved','Cancelled') OR NOT (
            (OLD."Status" = NEW."Status" AND OLD."Status" IN ('Open','Held','Disputed')) OR
            (OLD."Status", NEW."Status") IN (('Open','Held'),('Open','Disputed'),('Held','Open'),('Disputed','Open'),
                ('Open','Resolved'),('Held','Resolved'),('Disputed','Resolved'),
                ('Open','Cancelled'),('Held','Cancelled'),('Disputed','Cancelled')))
           OR (new_data - ARRAY['Status','CurrentStage','CurrentExposure','NextActionOn','AssignedTo','PromiseAmount',
                'PromiseDueOn','UpdatedBy','UpdatedOn','StatusReason','EvidenceReference','Version'])
              IS DISTINCT FROM
              (old_data - ARRAY['Status','CurrentStage','CurrentExposure','NextActionOn','AssignedTo','PromiseAmount',
                'PromiseDueOn','UpdatedBy','UpdatedOn','StatusReason','EvidenceReference','Version'])
           OR NEW."UpdatedBy" IS NULL OR NEW."UpdatedOn" IS NULL THEN
            RAISE EXCEPTION 'invalid or immutable dunning case transition' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'PromisesToPay' THEN
        IF OLD."Status" = 'Kept' AND NEW."Status" = 'Broken' THEN
            IF (new_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version'])
               OR NEW."ClosedBy" IS NULL OR NEW."ClosedOn" IS NULL
               OR NEW."ClosureEvidenceReference" IS NULL
               OR NEW."MatchedPaymentId" IS NOT NULL OR NEW."MatchedAmount" IS NOT NULL THEN
                RAISE EXCEPTION 'invalid kept-promise accounting reversal' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" <> 'Open' OR NEW."Status" NOT IN ('Kept','Broken','Withdrawn')
           OR (new_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version'])
              IS DISTINCT FROM
              (old_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version']) THEN
            RAISE EXCEPTION 'invalid or immutable promise transition' USING ERRCODE = '55000';
        END IF;
        IF NEW."Status" = 'Kept' THEN
            SELECT p."Amount" - COALESCE(SUM(r."Amount") FILTER (
                WHERE r."Status" = 'Released' AND r."ReleasedOn" <= NEW."ClosedOn"
                  AND (r."ReversedOn" IS NULL OR r."ReversedOn" > NEW."ClosedOn")), 0)
            INTO payment_amount
            FROM public."CustomerPayments" p
            JOIN public."DunningCases" c ON c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId"
            LEFT JOIN public."CustomerRefunds" r ON r."BusinessUnitId" = p."BusinessUnitId"
                AND r."SourcePaymentId" = p."Id"
            WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Id" = NEW."MatchedPaymentId"
              AND p."Status" = 'Posted' AND p."ReversedOn" IS NULL AND p."CustomerId" = c."CustomerId"
              AND p."CurrencyId" IS NOT DISTINCT FROM c."CurrencyId"
              AND p."PaymentDate" >= NEW."PromisedOn" AND p."PaymentDate" <= NEW."ClosedOn"
            GROUP BY p."Amount";
            IF payment_amount IS NULL OR NEW."MatchedAmount" < NEW."Amount" OR NEW."MatchedAmount" > payment_amount THEN
                RAISE EXCEPTION 'a kept promise requires a matching posted tenant payment' USING ERRCODE = '23514';
            END IF;
        END IF;
    ELSIF TG_TABLE_NAME = 'DunningNotices' THEN
        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
            IF (new_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version']) IS DISTINCT FROM
               (old_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version'])
               OR NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy" = OLD."CreatedBy" THEN
                RAISE EXCEPTION 'invalid notice approval' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" IN ('Draft','Approved','Failed') AND NEW."Status" = 'Released' THEN
            SELECT step."RequiresApproval" INTO requires_approval
            FROM public."DunningCases" c
            JOIN public."DunningPolicySteps" step
              ON step."BusinessUnitId" = c."BusinessUnitId" AND step."DunningPolicyId" = c."DunningPolicyId"
             AND step."Stage" = NEW."Stage"
            WHERE c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId";
            IF (new_data - ARRAY['Status','ReleasedBy','ReleasedOn','ProviderReference','FailureCode','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','ReleasedBy','ReleasedOn','ProviderReference','FailureCode','Version'])
               OR NEW."ReleasedBy" IS NULL OR NEW."ReleasedBy" = OLD."CreatedBy"
               OR NEW."ReleasedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
               OR NEW."ReleasedOn" < (clock_timestamp() AT TIME ZONE 'UTC') - interval '5 minutes'
               OR NEW."ReleasedOn" > (clock_timestamp() AT TIME ZONE 'UTC') + interval '1 minute'
               OR NEW."ProviderReference" IS NOT NULL OR NEW."FailureCode" IS NOT NULL
               OR requires_approval IS NULL
               OR (requires_approval AND OLD."Status" = 'Draft')
               OR NOT EXISTS (
                    SELECT 1 FROM public."DunningCases" c
                    JOIN public."CustomerStatements" statement
                      ON statement."BusinessUnitId" = c."BusinessUnitId"
                     AND statement."Id" = c."CustomerStatementId"
                     AND statement."Status" = 'Finalized'
                    JOIN public."DunningPolicies" policy
                      ON policy."BusinessUnitId" = c."BusinessUnitId"
                     AND policy."Id" = c."DunningPolicyId" AND policy."Status" = 'Active'
                    JOIN public."FinanceCommunicationContacts" contact
                      ON contact."BusinessUnitId" = c."BusinessUnitId"
                     AND contact."Id" = NEW."FinanceCommunicationContactId"
                     AND contact."CustomerId" = c."CustomerId"
                     AND contact."IsActive" AND contact."IsVerified"
                     AND contact."Purpose" = 'Collections'
                     AND contact."EffectiveFrom" <= (clock_timestamp() AT TIME ZONE 'UTC')
                     AND (contact."EffectiveTo" IS NULL OR contact."EffectiveTo" > (clock_timestamp() AT TIME ZONE 'UTC'))
                    JOIN public."DunningPolicySteps" release_step
                      ON release_step."BusinessUnitId" = c."BusinessUnitId"
                     AND release_step."DunningPolicyId" = c."DunningPolicyId"
                     AND release_step."Stage" = NEW."Stage"
                     AND release_step."Channel" = contact."Channel"
                     AND release_step."TemplateVersion" = NEW."TemplateVersion"
                    WHERE c."BusinessUnitId" = NEW."BusinessUnitId"
                      AND c."Id" = NEW."DunningCaseId" AND c."Status" = 'Open'
                      AND NEW."CustomerStatementId" = c."CustomerStatementId") THEN
                RAISE EXCEPTION 'invalid notice release' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Released' AND NEW."Status" IN ('Delivered','Failed') THEN
            IF (new_data - ARRAY['Status','DeliveryUpdatedBy','DeliveryUpdatedOn','ProviderReference','FailureCode','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','DeliveryUpdatedBy','DeliveryUpdatedOn','ProviderReference','FailureCode','Version']) THEN
                RAISE EXCEPTION 'invalid notice delivery result' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" IN ('Draft','Approved','Released','Failed') AND NEW."Status" = 'Cancelled' THEN
            IF (new_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','CancellationEvidenceReference','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','CancellationEvidenceReference','Version']) THEN
                RAISE EXCEPTION 'invalid notice cancellation' USING ERRCODE = '55000';
            END IF;
        ELSE
            RAISE EXCEPTION 'invalid or immutable dunning notice transition' USING ERRCODE = '55000';
        END IF;
    ELSIF TG_TABLE_NAME = 'DunningRuns' THEN
        IF OLD."Status" = 'Pending' AND NEW."Status" = 'Running' THEN
            IF (new_data - ARRAY['Status','LeaseOwner','LeaseToken','LeaseUntil','Version']) IS DISTINCT FROM
               (old_data - ARRAY['Status','LeaseOwner','LeaseToken','LeaseUntil','Version']) THEN
                RAISE EXCEPTION 'invalid dunning run start' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Running' AND NEW."Status" = 'Running' THEN
            IF NEW."LeaseOwner" = OLD."LeaseOwner" AND NEW."LeaseToken" = OLD."LeaseToken" THEN
                IF (new_data - ARRAY['LeaseUntil','Version']) IS DISTINCT FROM
                   (old_data - ARRAY['LeaseUntil','Version'])
                   OR OLD."LeaseUntil" < (clock_timestamp() AT TIME ZONE 'UTC')
                   OR NEW."LeaseUntil" <= OLD."LeaseUntil"
                   OR NEW."LeaseUntil" <= (clock_timestamp() AT TIME ZONE 'UTC') THEN
                    RAISE EXCEPTION 'invalid dunning run lease heartbeat' USING ERRCODE = '55000';
                END IF;
            ELSIF OLD."LeaseUntil" >= (clock_timestamp() AT TIME ZONE 'UTC')
               OR (new_data - ARRAY['LeaseOwner','LeaseToken','LeaseUntil','Version']) IS DISTINCT FROM
                  (old_data - ARRAY['LeaseOwner','LeaseToken','LeaseUntil','Version'])
               OR NEW."LeaseToken" IS NOT DISTINCT FROM OLD."LeaseToken"
               OR NEW."LeaseUntil" <= (clock_timestamp() AT TIME ZONE 'UTC') THEN
                RAISE EXCEPTION 'only an expired dunning run lease can be recovered' USING ERRCODE = '55000';
            END IF;
        ELSIF OLD."Status" = 'Running' AND NEW."Status" IN ('Completed','Failed') THEN
            IF (new_data - ARRAY['Status','CandidateCount','NoticeCount','SuppressedCount','FailedCount',
                    'LeaseOwner','LeaseToken','LeaseUntil','CompletedOn','CompletionEvidenceReference',
                    'FailureReason','FailureEvidenceReference','Version'])
               IS DISTINCT FROM
               (old_data - ARRAY['Status','CandidateCount','NoticeCount','SuppressedCount','FailedCount',
                    'LeaseOwner','LeaseToken','LeaseUntil','CompletedOn','CompletionEvidenceReference',
                    'FailureReason','FailureEvidenceReference','Version'])
               OR OLD."LeaseUntil" < (clock_timestamp() AT TIME ZONE 'UTC') THEN
                RAISE EXCEPTION 'invalid dunning run completion' USING ERRCODE = '55000';
            END IF;
        ELSE
            RAISE EXCEPTION 'invalid or immutable dunning run transition' USING ERRCODE = '55000';
        END IF;
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_ar_reconcile_kept_promise_payment(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ar_reconcile_kept_promise_payment() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE row_data jsonb := to_jsonb(NEW);
DECLARE prior_data jsonb := to_jsonb(OLD);
DECLARE business_unit_id bigint := (to_jsonb(NEW)->>'BusinessUnitId')::bigint;
DECLARE accounting_actor text;
DECLARE accounting_time timestamp without time zone;
DECLARE payment_id bigint;
BEGIN
    IF TG_TABLE_NAME = 'CustomerPayments'
       AND (row_data->>'Status' = 'Reversed' OR row_data->>'ReversedOn' IS NOT NULL)
       AND (prior_data->>'Status' IS DISTINCT FROM row_data->>'Status'
            OR prior_data->>'ReversedOn' IS DISTINCT FROM row_data->>'ReversedOn')
    THEN
        accounting_actor := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
            row_data->>'ReversedBy', 'payment-reversal');
        accounting_time := COALESCE((row_data->>'ReversedOn')::timestamp, now());
        payment_id := (row_data->>'Id')::bigint;
    ELSIF TG_TABLE_NAME = 'CustomerRefunds'
       AND row_data->>'Status' = 'Released' AND row_data->>'ReversedOn' IS NULL
       AND (prior_data->>'Status' IS DISTINCT FROM row_data->>'Status'
            OR prior_data->>'ReleasedOn' IS DISTINCT FROM row_data->>'ReleasedOn')
    THEN
        payment_id := (row_data->>'SourcePaymentId')::bigint;
        IF NOT EXISTS (
            SELECT 1 FROM public."PromisesToPay" promise
            JOIN public."CustomerPayments" payment
              ON payment."BusinessUnitId" = promise."BusinessUnitId" AND payment."Id" = promise."MatchedPaymentId"
            WHERE promise."BusinessUnitId" = business_unit_id AND promise."MatchedPaymentId" = payment_id
              AND promise."Status" = 'Kept'
              AND payment."Amount" - (COALESCE((SELECT SUM(existing."Amount")
                  FROM public."CustomerRefunds" existing
                  WHERE existing."BusinessUnitId" = business_unit_id
                    AND existing."SourcePaymentId" = payment_id
                    AND existing."Id" <> (row_data->>'Id')::bigint
                    AND existing."Status" = 'Released' AND existing."ReversedOn" IS NULL), 0)
                  + (row_data->>'Amount')::numeric) < promise."Amount") THEN
            RETURN NEW;
        END IF;
        accounting_actor := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
            row_data->>'ReleasedBy', 'refund-release');
        accounting_time := COALESCE((row_data->>'ReleasedOn')::timestamp, now());
    END IF;
    IF payment_id IS NOT NULL THEN
        UPDATE public."PromisesToPay" promise
        SET "Status" = 'Broken', "ClosedBy" = accounting_actor, "ClosedOn" = accounting_time,
            "ClosureEvidenceReference" = 'payment-accounting-reversal:' || TG_TABLE_NAME || ':' || (row_data->>'Id'),
            "MatchedPaymentId" = NULL, "MatchedAmount" = NULL, "Version" = "Version" + 1
        WHERE promise."BusinessUnitId" = business_unit_id
          AND promise."MatchedPaymentId" = payment_id AND promise."Status" = 'Kept';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_ar_validate_tenant_reference(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ar_validate_tenant_reference() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE row_data jsonb := to_jsonb(NEW);
DECLARE prior_data jsonb := CASE WHEN TG_OP = 'UPDATE' THEN to_jsonb(OLD) ELSE '{}'::jsonb END;
DECLARE customer_id bigint;
DECLARE currency_id bigint;
BEGIN
    IF NEW."BusinessUnitId" <= 0 THEN
        RAISE EXCEPTION 'a valid business unit is required' USING ERRCODE = '23514';
    END IF;
    IF row_data ? 'CustomerId' THEN
        customer_id := NULLIF(row_data->>'CustomerId', '')::bigint;
        IF customer_id IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."Customers" c WHERE c."ID" = customer_id
              AND (c."BUID" = NEW."BusinessUnitId" OR c."BUID" IS NULL)) THEN
            RAISE EXCEPTION 'the tenant customer does not exist' USING ERRCODE = '23503';
        END IF;
    END IF;
    IF row_data ? 'CurrencyId' THEN
        currency_id := NULLIF(row_data->>'CurrencyId', '')::bigint;
        IF currency_id IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."Currency" c WHERE c."ID" = currency_id
              AND c."BusinessUnitID" = NEW."BusinessUnitId") THEN
            RAISE EXCEPTION 'the tenant currency does not exist' USING ERRCODE = '23503';
        END IF;
    END IF;
    IF TG_TABLE_NAME = 'DunningCases' THEN
        IF NOT EXISTS (
            SELECT 1 FROM public."CustomerStatements" statement
            JOIN public."DunningPolicies" policy
              ON policy."BusinessUnitId" = NEW."BusinessUnitId" AND policy."Id" = NEW."DunningPolicyId"
            WHERE statement."BusinessUnitId" = NEW."BusinessUnitId"
              AND statement."Id" = NEW."CustomerStatementId"
              AND statement."CustomerId" = NEW."CustomerId"
              AND statement."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId"
              AND statement."Status" = 'Finalized' AND policy."Status" = 'Active') THEN
            RAISE EXCEPTION 'the dunning case customer, currency, statement, and active policy do not form one tenant accounting chain'
                USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'DunningRunDecisions' THEN
        IF NOT EXISTS (SELECT 1 FROM public."DunningRuns" r
            WHERE r."BusinessUnitId" = NEW."BusinessUnitId" AND r."Id" = NEW."DunningRunId") THEN
            RAISE EXCEPTION 'the tenant dunning run does not exist' USING ERRCODE = '23503';
        END IF;
        IF NEW."CustomerStatementId" IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."CustomerStatements" s
            WHERE s."BusinessUnitId" = NEW."BusinessUnitId" AND s."Id" = NEW."CustomerStatementId"
              AND s."CustomerId" = NEW."CustomerId" AND s."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId") THEN
            RAISE EXCEPTION 'the decision statement does not match its tenant customer and currency' USING ERRCODE = '23514';
        END IF;
        IF NEW."DunningCaseId" IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."DunningCases" c
            WHERE c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId"
              AND c."CustomerId" = NEW."CustomerId" AND c."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId") THEN
            RAISE EXCEPTION 'the decision case does not match its tenant customer and currency' USING ERRCODE = '23514';
        END IF;
        IF NEW."DunningNoticeId" IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."DunningNotices" n
            JOIN public."DunningCases" c ON c."BusinessUnitId" = n."BusinessUnitId" AND c."Id" = n."DunningCaseId"
            WHERE n."BusinessUnitId" = NEW."BusinessUnitId" AND n."Id" = NEW."DunningNoticeId"
              AND c."CustomerId" = NEW."CustomerId" AND c."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId"
              AND (NEW."DunningCaseId" IS NULL OR n."DunningCaseId" = NEW."DunningCaseId")
              AND (NEW."CustomerStatementId" IS NULL OR n."CustomerStatementId" = NEW."CustomerStatementId")) THEN
            RAISE EXCEPTION 'the decision notice does not match its tenant evidence chain' USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'DunningNotices' THEN
        IF NOT EXISTS (
            SELECT 1
            FROM public."DunningCases" c
            JOIN public."CustomerStatements" s
              ON s."BusinessUnitId" = c."BusinessUnitId" AND s."Id" = NEW."CustomerStatementId"
             AND s."Id" = c."CustomerStatementId"
             AND s."CustomerId" = c."CustomerId"
             AND s."CurrencyId" IS NOT DISTINCT FROM c."CurrencyId"
            JOIN public."FinanceCommunicationContacts" contact
              ON contact."BusinessUnitId" = c."BusinessUnitId" AND contact."Id" = NEW."FinanceCommunicationContactId"
             AND contact."CustomerId" = c."CustomerId"
             AND contact."IsActive" AND contact."IsVerified" AND contact."Purpose" = 'Collections'
             AND contact."EffectiveFrom" <= NEW."CreatedOn"
             AND (contact."EffectiveTo" IS NULL OR contact."EffectiveTo" > NEW."CreatedOn")
            JOIN public."DunningPolicySteps" step
             ON step."BusinessUnitId" = c."BusinessUnitId" AND step."DunningPolicyId" = c."DunningPolicyId"
             AND step."Stage" = NEW."Stage"
             AND step."Channel" = contact."Channel"
            WHERE c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId") THEN
            RAISE EXCEPTION 'the notice customer, statement, contact, case, and policy step do not form one tenant evidence chain'
                USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'DunningDeliveryAttempts' THEN
        IF NOT EXISTS (
            SELECT 1 FROM public."DunningNotices" n
            WHERE n."BusinessUnitId" = NEW."BusinessUnitId"
              AND n."Id" = NEW."DunningNoticeId"
              AND n."ArtifactHash" = NEW."ArtifactHash"
              AND n."TemplateVersion" = NEW."TemplateVersion") THEN
            RAISE EXCEPTION 'delivery evidence does not match the governed notice artifact' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_ar_verify_provider_evidence(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ar_verify_provider_evidence() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE provider_secret text;
DECLARE canonical text;
DECLARE expected_signature text;
BEGIN
    IF TG_TABLE_NAME = 'FinanceCommunicationContacts' THEN
        SELECT "Secret" INTO provider_secret FROM public."FinanceProviderSecrets"
         WHERE "Name" = 'ContactVerification';
        canonical := NEW."BusinessUnitId"::text || E'\n' || NEW."CustomerId"::text || E'\n'
            || NEW."Purpose" || E'\n' || NEW."Channel" || E'\n' || NEW."DestinationToken" || E'\n'
            || NEW."MaskedDestination" || E'\n'
            || floor(extract(epoch FROM NEW."EffectiveFrom" AT TIME ZONE 'UTC') * 1000)::bigint::text || E'\n'
            || CASE WHEN NEW."EffectiveTo" IS NULL THEN '' ELSE
                floor(extract(epoch FROM NEW."EffectiveTo" AT TIME ZONE 'UTC') * 1000)::bigint::text END || E'\n'
            || NEW."VerificationEvidenceReference" || E'\n' || NEW."VerificationProviderEventId"::text;
    ELSE
        SELECT "Secret" INTO provider_secret FROM public."FinanceProviderSecrets"
         WHERE "Name" = 'DunningDelivery';
        canonical := NEW."BusinessUnitId"::text || E'\n' || NEW."DunningNoticeId"::text || E'\n'
            || CASE WHEN NEW."Status" = 'Delivered' THEN 'true' ELSE 'false' END || E'\n'
            || NEW."ProviderEventId"::text || E'\n' || coalesce(NEW."ProviderReference", '') || E'\n'
            || floor(extract(epoch FROM NEW."ProviderOccurredOn" AT TIME ZONE 'UTC') * 1000)::bigint::text || E'\n'
            || coalesce(NEW."FailureCode", '') || E'\n' || NEW."SignedEvidenceReference";
    END IF;
    IF provider_secret IS NULL THEN
        RAISE EXCEPTION 'finance provider verification secret is not configured' USING ERRCODE = '55000';
    END IF;
    expected_signature := encode(hmac(convert_to(canonical, 'UTF8'),
        convert_to(provider_secret, 'UTF8'), 'sha256'), 'hex');
    IF NEW."ProviderSignature" IS DISTINCT FROM expected_signature THEN
        RAISE EXCEPTION 'finance provider evidence signature is invalid' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_ar_verify_run_decision_profile(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_ar_verify_run_decision_profile() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM public."CustomerCollectionProfiles" profile
        JOIN public."DunningRuns" run
          ON run."BusinessUnitId" = profile."BusinessUnitId"
         AND run."Id" = NEW."DunningRunId"
         AND run."DunningPolicyId" = profile."DunningPolicyId"
        WHERE profile."BusinessUnitId" = NEW."BusinessUnitId"
          AND profile."Id" = NEW."CustomerCollectionProfileId"
          AND profile."CustomerId" = NEW."CustomerId"
          AND profile."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId") THEN
        RAISE EXCEPTION 'the dunning decision profile does not match its run, customer, and currency checkpoint'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_assign_commercial_case(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_assign_commercial_case() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    cfg "LeadReferenceConfigurations"%ROWTYPE;
    created_at timestamp;
    allocation_number bigint;
    financial_year_start integer;
    financial_year_label text;
    generated_reference text;
    business_unit_code text;
    commercial_case_id bigint;
BEGIN
    IF NEW."CommercialCaseId" IS NOT NULL OR NEW."CommercialCaseReference" IS NOT NULL THEN
        RAISE EXCEPTION 'Commercial-case identity is generated by the server and cannot be supplied manually.';
    END IF;

    SELECT * INTO cfg FROM "LeadReferenceConfigurations"
    WHERE "BusinessUnitID" = NEW."BusinessUnitID";
    IF NOT FOUND THEN
        INSERT INTO "LeadReferenceConfigurations"
            ("BusinessUnitID", "Prefix", "Format", "SequencePadding", "FinancialYearStartMonth", "CreatedOn")
        VALUES (NEW."BusinessUnitID", 'NXR', '{PREFIX}-{YEAR}-{SEQUENCE}', 6, 1, now())
        ON CONFLICT ("BusinessUnitID") DO NOTHING;
        SELECT * INTO cfg FROM "LeadReferenceConfigurations"
        WHERE "BusinessUnitID" = NEW."BusinessUnitID";
    END IF;

    created_at := COALESCE(NEW."CreatedDate", now());
    allocation_number := nextval('"CommercialCaseReferenceSequence"');
    financial_year_start := CASE
        WHEN extract(month FROM created_at) < cfg."FinancialYearStartMonth" THEN extract(year FROM created_at)::integer - 1
        ELSE extract(year FROM created_at)::integer
    END;
    financial_year_label := financial_year_start::text || '-' || right((financial_year_start + 1)::text, 2);

    SELECT regexp_replace(upper("BusinessUnitCode"), '[^A-Z0-9_-]', '', 'g')
    INTO business_unit_code FROM "BusinessUnits" WHERE "ID" = NEW."BusinessUnitID";

    generated_reference := cfg."Format";
    generated_reference := replace(generated_reference, '{PREFIX}', regexp_replace(upper(cfg."Prefix"), '[^A-Z0-9_-]', '', 'g'));
    generated_reference := replace(generated_reference, '{YEAR}', to_char(created_at, 'YYYY'));
    generated_reference := replace(generated_reference, '{FY}', financial_year_label);
    generated_reference := replace(generated_reference, '{BU}', COALESCE(business_unit_code, ''));
    generated_reference := replace(generated_reference, '{SOURCE}', regexp_replace(upper(COALESCE(NEW."LeadSource", '')), '[^A-Z0-9_-]', '', 'g'));
    generated_reference := replace(generated_reference, '{SEQUENCE}', lpad(allocation_number::text, cfg."SequencePadding", '0'));

    IF length(generated_reference) = 0 OR length(generated_reference) > 100
       OR position('{' in generated_reference) > 0 OR position('}' in generated_reference) > 0 THEN
        RAISE EXCEPTION 'Lead-reference configuration produced an invalid reference.';
    END IF;

    INSERT INTO "CommercialCases"
        ("BusinessUnitID", "AllocationNumber", "MasterReference", "CreatedOn", "CreatedBy")
    VALUES
        (NEW."BusinessUnitID", allocation_number, generated_reference, created_at,
         COALESCE(NULLIF(NEW."CreatedBy", ''), 'System'))
    RETURNING "Id" INTO commercial_case_id;

    NEW."CommercialCaseId" := commercial_case_id;
    NEW."CommercialCaseReference" := generated_reference;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_bank_certify_run(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_certify_run() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE line_count integer; DECLARE journal_count integer; DECLARE incomplete_count integer;
DECLARE matched numeric(18,2); DECLARE book_balance numeric(18,2); DECLARE canonical text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'reconciliation runs cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."Version" <> 1 OR NEW."CertificateHash" IS NOT NULL THEN
            RAISE EXCEPTION 'reconciliation runs must begin as uncertified drafts' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
       OR NEW."BankAccountId" <> OLD."BankAccountId" OR NEW."BankStatementId" <> OLD."BankStatementId"
       OR NEW."ReconciliationThrough" <> OLD."ReconciliationThrough"
       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
       OR NEW."PreparedBy" <> OLD."PreparedBy" OR NEW."PreparedOn" <> OLD."PreparedOn"
       OR NEW."Version" <> OLD."Version" + 1
       OR NOT ((OLD."Status" IN ('Draft','Reopened') AND NEW."Status" = 'InReview')
            OR (OLD."Status" = 'InReview' AND NEW."Status" = 'Approved')
            OR (OLD."Status" = 'Approved' AND NEW."Status" = 'Reopened')) THEN
        RAISE EXCEPTION 'invalid reconciliation-run transition' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" = 'InReview' AND NEW."Status" = 'Approved' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:bank-reconciliation:' || NEW."BusinessUnitId"::text || ':' || NEW."BankAccountId"::text, 0));
        IF NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy" = NEW."PreparedBy"
           OR NEW."ApprovedBy" = NEW."SubmittedBy"
           OR length(trim(NEW."ApprovalReason")) < 10 OR length(trim(NEW."EvidenceReference")) < 8 THEN
            RAISE EXCEPTION 'independent approval with reason and evidence is required' USING ERRCODE = '23514';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM public."BankStatements" statement
            WHERE statement."BusinessUnitId" = NEW."BusinessUnitId" AND statement."Id" = NEW."BankStatementId"
              AND statement."ClosingBalance" = NEW."BankClosingBalance") THEN
            RAISE EXCEPTION 'run bank balance must equal immutable statement closing balance' USING ERRCODE = '23514';
        END IF;
        SELECT count(*), COALESCE(sum(abs(line."SignedAmount")),0),
               count(*) FILTER (WHERE COALESCE(confirmed.amount,0) <> abs(line."SignedAmount"))
        INTO line_count, matched, incomplete_count
        FROM public."BankStatementLines" line
        LEFT JOIN (SELECT a."BankStatementLineId", sum(a."BankAmount") amount
            FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
              ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
            WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id"
              AND m."Status" = 'Confirmed' GROUP BY a."BankStatementLineId") confirmed ON confirmed."BankStatementLineId" = line."Id"
        WHERE line."BusinessUnitId" = NEW."BusinessUnitId" AND line."BankStatementId" = NEW."BankStatementId";
        IF line_count = 0 OR incomplete_count <> 0 OR EXISTS (SELECT 1 FROM public."ReconciliationMatches"
            WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "ReconciliationRunId" = NEW."Id" AND "Status" = 'Proposed') THEN
            RAISE EXCEPTION 'all statement lines must be exactly confirmed before approval' USING ERRCODE = '23514';
        END IF;
        SELECT count(DISTINCT jl."JournalEntryId") INTO journal_count
        FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
          ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
        JOIN public."JournalEntryLines" jl ON jl."BusinessUnitId" = a."BusinessUnitId" AND jl."Id" = a."JournalEntryLineId"
        WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id" AND m."Status" = 'Confirmed';
        IF EXISTS (SELECT 1 FROM public."ReconciliationAllocations" a
            JOIN public."ReconciliationMatches" m ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
            JOIN public."JournalEntryLines" jl ON jl."BusinessUnitId" = a."BusinessUnitId" AND jl."Id" = a."JournalEntryLineId"
            JOIN public."JournalEntries" journal ON journal."BusinessUnitId" = jl."BusinessUnitId" AND journal."Id" = jl."JournalEntryId"
            JOIN public."BankAccounts" account ON account."BusinessUnitId" = jl."BusinessUnitId" AND account."Id" = NEW."BankAccountId"
            WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id"
              AND m."Status" = 'Confirmed' AND (journal."Status" <> 'Posted'
                OR journal."AccountingDate" > NEW."ReconciliationThrough"
                OR jl."LedgerAccountId" <> account."LedgerAccountId")) THEN
            RAISE EXCEPTION 'all allocated journals must remain posted, timely, and on the bank ledger account' USING ERRCODE = '23514';
        END IF;
        SELECT COALESCE(sum(jl."FunctionalDebit" - jl."FunctionalCredit"),0) INTO book_balance
        FROM public."JournalEntryLines" jl JOIN public."JournalEntries" journal
          ON journal."BusinessUnitId" = jl."BusinessUnitId" AND journal."Id" = jl."JournalEntryId"
        JOIN public."BankAccounts" account ON account."BusinessUnitId" = jl."BusinessUnitId"
          AND account."Id" = NEW."BankAccountId" AND account."LedgerAccountId" = jl."LedgerAccountId"
        WHERE journal."BusinessUnitId" = NEW."BusinessUnitId" AND journal."Status" = 'Posted'
          AND journal."AccountingDate" <= NEW."ReconciliationThrough";
        IF book_balance <> NEW."BankClosingBalance" THEN
            RAISE EXCEPTION 'bank and book closing balances must agree before approval' USING ERRCODE = '23514';
        END IF;
        SELECT string_agg(line."LineFingerprint" || ':' || a."JournalEntryLineId"::text || ':'
            || to_char(a."BankAmount", 'FM9999999999999990.00') || ':' || to_char(a."FunctionalAmount", 'FM9999999999999990.00'),
            '|' ORDER BY line."LineFingerprint", a."JournalEntryLineId") INTO canonical
        FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
          ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
        JOIN public."BankStatementLines" line ON line."BusinessUnitId" = a."BusinessUnitId" AND line."Id" = a."BankStatementLineId"
        WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id" AND m."Status" = 'Confirmed';
        NEW."MatchedAmount" := matched; NEW."UnexplainedDifference" := 0;
        NEW."BookClosingBalance" := book_balance; NEW."CertificateLineCount" := line_count;
        NEW."CertificateJournalCount" := journal_count;
        NEW."CertificateHash" := encode(digest(convert_to(COALESCE(canonical,'') || ':' || NEW."BankClosingBalance"::text, 'UTF8'), 'sha256'), 'hex');
    ELSIF OLD."Status" = 'Approved' THEN
        IF NEW."ReopenedBy" IS NULL OR NEW."ReopenedBy" = NEW."ApprovedBy"
           OR length(trim(NEW."ReopenReason")) < 10 OR length(trim(NEW."ReopenEvidenceReference")) < 8
           OR NEW."BankClosingBalance" <> OLD."BankClosingBalance"
           OR NEW."BookClosingBalance" <> OLD."BookClosingBalance"
           OR NEW."MatchedAmount" <> OLD."MatchedAmount"
           OR NEW."UnexplainedDifference" <> OLD."UnexplainedDifference"
           OR NEW."SubmittedBy" IS DISTINCT FROM OLD."SubmittedBy"
           OR NEW."SubmittedOn" IS DISTINCT FROM OLD."SubmittedOn"
           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy"
           OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
           OR NEW."ApprovalReason" IS DISTINCT FROM OLD."ApprovalReason"
           OR NEW."EvidenceReference" IS DISTINCT FROM OLD."EvidenceReference"
           OR NEW."CertificateHash" IS DISTINCT FROM OLD."CertificateHash"
           OR NEW."CertificateLineCount" IS DISTINCT FROM OLD."CertificateLineCount"
           OR NEW."CertificateJournalCount" IS DISTINCT FROM OLD."CertificateJournalCount" THEN
            RAISE EXCEPTION 'reopening requires independent evidence and preserves the certificate' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW."Status" = 'InReview' AND (NEW."SubmittedBy" IS NULL OR NEW."SubmittedOn" IS NULL) THEN
        RAISE EXCEPTION 'submission requires an identified submitter and timestamp' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_bank_check_match_trigger(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_check_match_trigger() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE target_match_id bigint;
BEGIN
    IF TG_TABLE_NAME = 'ReconciliationMatches' THEN
        target_match_id := NEW."Id";
    ELSE
        target_match_id := NEW."ReconciliationMatchId";
    END IF;
    PERFORM public.nexora_bank_validate_match(target_match_id);
    RETURN NULL;
END
$$;


--
-- Name: nexora_bank_evidence_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_evidence_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE aggregate_type text; DECLARE aggregate_version bigint; DECLARE action_name text;
DECLARE event_name text; DECLARE actor_id text; DECLARE occurred_at timestamp without time zone;
DECLARE payload jsonb; DECLARE event_id uuid; DECLARE seed text;
BEGIN
    aggregate_type := CASE TG_TABLE_NAME
        WHEN 'BankAccounts' THEN 'BankAccount'
        WHEN 'BankStatementImports' THEN 'BankStatementImport'
        WHEN 'ReconciliationRuns' THEN 'ReconciliationRun'
        WHEN 'ReconciliationMatches' THEN 'ReconciliationMatch'
        WHEN 'BankMatchingRules' THEN 'BankMatchingRule'
        WHEN 'BankAdjustments' THEN 'BankAdjustment'
        ELSE TG_TABLE_NAME END;
    aggregate_version := COALESCE((to_jsonb(NEW)->>'Version')::bigint,
        (to_jsonb(NEW)->>'RecordVersion')::bigint, 1);
    action_name := CASE WHEN TG_OP = 'INSERT' THEN 'Created'
        ELSE COALESCE(to_jsonb(NEW)->>'Status', 'Updated') END;
    actor_id := CASE to_jsonb(NEW)->>'Status'
        WHEN 'Reversed' THEN to_jsonb(NEW)->>'ReversedBy'
        WHEN 'Retired' THEN to_jsonb(NEW)->>'RetiredBy'
        WHEN 'Active' THEN to_jsonb(NEW)->>'ActivatedBy'
        WHEN 'Rejected' THEN to_jsonb(NEW)->>'RejectedBy'
        WHEN 'Cancelled' THEN to_jsonb(NEW)->>'CancelledBy'
        WHEN 'Reopened' THEN to_jsonb(NEW)->>'ReopenedBy'
        WHEN 'Confirmed' THEN to_jsonb(NEW)->>'ConfirmedBy'
        WHEN 'Voided' THEN to_jsonb(NEW)->>'VoidedBy'
        WHEN 'InReview' THEN to_jsonb(NEW)->>'SubmittedBy'
        WHEN 'Posted' THEN to_jsonb(NEW)->>'ApprovedBy'
        WHEN 'Approved' THEN to_jsonb(NEW)->>'ApprovedBy'
        ELSE NULL END;
    actor_id := COALESCE(actor_id, to_jsonb(NEW)->>'StatusChangedBy',
        to_jsonb(NEW)->>'ImportedBy', to_jsonb(NEW)->>'CreatedBy',
        to_jsonb(NEW)->>'PreparedBy', 'system:treasury');
    occurred_at := clock_timestamp() AT TIME ZONE 'UTC'; payload := to_jsonb(NEW) - 'RawPayload';
    IF TG_TABLE_NAME = 'BankAdjustments' THEN
        payload := payload || jsonb_build_object('Distributions', COALESCE((SELECT jsonb_agg(to_jsonb(distribution)
            ORDER BY distribution."Sequence") FROM public."BankAdjustmentDistributions" distribution
            WHERE distribution."BusinessUnitId" = NEW."BusinessUnitId"
              AND distribution."BankAdjustmentId" = NEW."Id"), '[]'::jsonb));
    END IF;
    event_name := 'finance.' || lower(aggregate_type) || '.' || lower(action_name);
    seed := NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
        || ':' || aggregate_version::text || ':' || event_name;
    event_id := (substr(md5(seed),1,8)||'-'||substr(md5(seed),9,4)||'-4'||
        substr(md5(seed),14,3)||'-a'||substr(md5(seed),18,3)||'-'||substr(md5(seed),21,12))::uuid;
    INSERT INTO public."CommercialFinanceAudits"
        ("BusinessUnitId","AggregateType","AggregateId","Action","Actor","OccurredOn","DetailJson")
    VALUES (NEW."BusinessUnitId",aggregate_type,NEW."Id",action_name,actor_id,occurred_at,payload);
    INSERT INTO public."FinanceOutboxMessages"
        ("BusinessUnitId","EventId","AggregateType","AggregateId","AggregateVersion","EventType",
         "Payload","SchemaVersion","OccurredOn","AvailableOn","AttemptCount")
    VALUES (NEW."BusinessUnitId",event_id,aggregate_type,NEW."Id",aggregate_version,event_name,
        payload,1,occurred_at,occurred_at,0);
    RETURN NULL;
END $$;


--
-- Name: nexora_bank_guard_account(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_guard_account() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'bank accounts cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Active' OR NEW."Version" <> 1 THEN
            RAISE EXCEPTION 'bank accounts must begin active at version one' USING ERRCODE = '23514';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM public."LedgerBooks" book
            WHERE book."BusinessUnitId" = NEW."BusinessUnitId"
              AND book."FunctionalCurrencyId" = NEW."CurrencyId") THEN
            RAISE EXCEPTION 'bank account currency must equal the accounting book functional currency' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
       OR NEW."Name" <> OLD."Name" OR NEW."InstitutionName" <> OLD."InstitutionName"
       OR NEW."MaskedAccountNumber" <> OLD."MaskedAccountNumber"
       OR NEW."AccountFingerprint" <> OLD."AccountFingerprint"
       OR NEW."CurrencyId" <> OLD."CurrencyId" OR NEW."LedgerAccountId" <> OLD."LedgerAccountId"
       OR NEW."OpeningDate" <> OLD."OpeningDate" OR NEW."IdempotencyKey" <> OLD."IdempotencyKey"
       OR NEW."RequestHash" <> OLD."RequestHash" OR NEW."CreatedBy" <> OLD."CreatedBy"
       OR NEW."CreatedOn" <> OLD."CreatedOn" OR NEW."Version" <> OLD."Version" + 1
       OR NEW."StatusChangedBy" IS NULL OR NEW."StatusChangedOn" IS NULL
       OR length(trim(NEW."StatusReason")) < 10
       OR NOT ((OLD."Status" = 'Active' AND NEW."Status" IN ('Suspended','Closed'))
            OR (OLD."Status" = 'Suspended' AND NEW."Status" IN ('Active','Closed'))) THEN
        RAISE EXCEPTION 'invalid governed bank-account transition' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_bank_guard_allocation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_guard_allocation() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'reconciliation allocations are append-only' USING ERRCODE = '55000';
    END IF;
    IF NEW."BankAmount" <> NEW."FunctionalAmount" THEN
        RAISE EXCEPTION 'functional-currency reconciliation requires equal allocation amounts' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public."ReconciliationMatches" match
        JOIN public."ReconciliationRuns" run ON run."BusinessUnitId" = match."BusinessUnitId"
          AND run."Id" = match."ReconciliationRunId"
        WHERE match."BusinessUnitId" = NEW."BusinessUnitId" AND match."Id" = NEW."ReconciliationMatchId"
          AND match."Status" = 'Proposed' AND run."Status" IN ('Draft','Reopened')) THEN
        RAISE EXCEPTION 'allocations can only be added to a proposed match in an editable run' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_bank_guard_import(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_guard_import() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF NEW."Status" <> 'Validated' OR NEW."RawPayload" IS NULL
       OR octet_length(NEW."RawPayload") NOT BETWEEN 1 AND 10485760
       OR encode(digest(NEW."RawPayload", 'sha256'), 'hex') <> NEW."SourceHash" THEN
        RAISE EXCEPTION 'validated imports require retained source bytes matching the source digest' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_bank_guard_match(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_guard_match() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'reconciliation matches cannot be deleted' USING ERRCODE = '55000'; END IF;
    IF TG_OP = 'INSERT' AND (NEW."Status" <> 'Proposed' OR NEW."Version" <> 1) THEN
        RAISE EXCEPTION 'matches must begin proposed at version one' USING ERRCODE = '23514';
    ELSIF TG_OP = 'INSERT' AND NOT EXISTS (SELECT 1 FROM public."ReconciliationRuns" run
        WHERE run."BusinessUnitId" = NEW."BusinessUnitId" AND run."Id" = NEW."ReconciliationRunId"
          AND run."Status" IN ('Draft','Reopened')) THEN
        RAISE EXCEPTION 'matches can only be added to an editable reconciliation' USING ERRCODE = '55000';
    ELSIF TG_OP = 'UPDATE' THEN
        IF NOT EXISTS (SELECT 1 FROM public."ReconciliationRuns" run
            WHERE run."BusinessUnitId" = NEW."BusinessUnitId" AND run."Id" = NEW."ReconciliationRunId"
              AND run."Status" IN ('Draft','Reopened')) THEN
            RAISE EXCEPTION 'matches can only change within an editable reconciliation' USING ERRCODE = '55000';
        END IF;
        IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
           OR NEW."ReconciliationRunId" <> OLD."ReconciliationRunId" OR NEW."MatchType" <> OLD."MatchType"
           OR NEW."Confidence" <> OLD."Confidence" OR NEW."RuleCode" <> OLD."RuleCode"
           OR NEW."RuleVersion" <> OLD."RuleVersion" OR NEW."IdempotencyKey" <> OLD."IdempotencyKey"
           OR NEW."RequestHash" <> OLD."RequestHash" OR NEW."CreatedBy" <> OLD."CreatedBy"
           OR NEW."CreatedOn" <> OLD."CreatedOn" OR NEW."Version" <> OLD."Version" + 1
           OR NOT ((OLD."Status" = 'Proposed' AND NEW."Status" IN ('Confirmed','Voided'))
                OR (OLD."Status" = 'Confirmed' AND NEW."Status" = 'Voided')) THEN
            RAISE EXCEPTION 'invalid reconciliation-match transition' USING ERRCODE = '55000';
        END IF;
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_bank_immutable_evidence(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_immutable_evidence() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    RAISE EXCEPTION 'bank statement evidence is append-only' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_bank_validate_match(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_validate_match(match_id bigint) RETURNS void
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE match_row record; DECLARE allocation_row record; DECLARE allocated numeric(18,2);
BEGIN
    SELECT m.*, r."BankStatementId", r."BankAccountId" INTO STRICT match_row
    FROM public."ReconciliationMatches" m JOIN public."ReconciliationRuns" r
      ON r."BusinessUnitId" = m."BusinessUnitId" AND r."Id" = m."ReconciliationRunId"
    WHERE m."Id" = match_id FOR UPDATE OF m, r;
    FOR allocation_row IN
        SELECT a.*, line."SignedAmount", journal_line."FunctionalDebit", journal_line."FunctionalCredit",
               journal_line."LedgerAccountId", journal."Status" AS journal_status,
               journal."AccountingDate" AS journal_date,
               account."LedgerAccountId" AS cash_account_id
        FROM public."ReconciliationAllocations" a
        JOIN public."BankStatementLines" line
          ON line."BusinessUnitId" = a."BusinessUnitId" AND line."Id" = a."BankStatementLineId"
        JOIN public."JournalEntryLines" journal_line
          ON journal_line."BusinessUnitId" = a."BusinessUnitId" AND journal_line."Id" = a."JournalEntryLineId"
        JOIN public."JournalEntries" journal
          ON journal."BusinessUnitId" = journal_line."BusinessUnitId" AND journal."Id" = journal_line."JournalEntryId"
        JOIN public."BankAccounts" account
          ON account."BusinessUnitId" = a."BusinessUnitId" AND account."Id" = match_row."BankAccountId"
        WHERE a."ReconciliationMatchId" = match_id ORDER BY a."BankStatementLineId", a."JournalEntryLineId"
    LOOP
        PERFORM 1 FROM public."BankStatementLines" WHERE "Id" = allocation_row."BankStatementLineId" FOR UPDATE;
        PERFORM 1 FROM public."JournalEntryLines" WHERE "Id" = allocation_row."JournalEntryLineId" FOR UPDATE;
        IF NOT EXISTS (SELECT 1 FROM public."BankStatementLines" line
            WHERE line."Id" = allocation_row."BankStatementLineId"
              AND line."BusinessUnitId" = match_row."BusinessUnitId"
              AND line."BankStatementId" = match_row."BankStatementId")
           OR allocation_row."LedgerAccountId" <> allocation_row.cash_account_id
           OR allocation_row.journal_status <> 'Posted'
           OR allocation_row.journal_date > (SELECT "ReconciliationThrough" FROM public."ReconciliationRuns" WHERE "Id" = match_row."ReconciliationRunId")
           OR (allocation_row."SignedAmount" > 0 AND allocation_row."FunctionalDebit" <= 0)
           OR (allocation_row."SignedAmount" < 0 AND allocation_row."FunctionalCredit" <= 0) THEN
            RAISE EXCEPTION 'allocation evidence is not eligible for this reconciliation' USING ERRCODE = '23514';
        END IF;
        IF match_row."Status" = 'Confirmed' THEN
            SELECT COALESCE(sum(a."BankAmount"),0) INTO allocated
            FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
              ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
            WHERE a."BusinessUnitId" = match_row."BusinessUnitId"
              AND a."BankStatementLineId" = allocation_row."BankStatementLineId" AND m."Status" = 'Confirmed';
            IF allocated > abs(allocation_row."SignedAmount") THEN
                RAISE EXCEPTION 'bank statement line is over-allocated' USING ERRCODE = '23514';
            END IF;
            SELECT COALESCE(sum(a."FunctionalAmount"),0) INTO allocated
            FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
              ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
            WHERE a."BusinessUnitId" = match_row."BusinessUnitId"
              AND a."JournalEntryLineId" = allocation_row."JournalEntryLineId" AND m."Status" = 'Confirmed';
            IF (allocation_row."SignedAmount" > 0 AND allocated > allocation_row."FunctionalDebit")
               OR (allocation_row."SignedAmount" < 0 AND allocated > allocation_row."FunctionalCredit") THEN
                RAISE EXCEPTION 'journal line is over-allocated' USING ERRCODE = '23514';
            END IF;
        END IF;
    END LOOP;
END
$$;


--
-- Name: nexora_bank_validate_statement(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_bank_validate_statement() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE calculated numeric(18,2);
BEGIN
    SELECT NEW."OpeningBalance" + COALESCE(sum(line."SignedAmount"),0) INTO calculated
    FROM public."BankStatementLines" line WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
      AND line."BankStatementId" = NEW."Id";
    IF calculated <> NEW."ClosingBalance" OR calculated <> NEW."CalculatedClosingBalance" THEN
        RAISE EXCEPTION 'statement lines do not reconcile opening and closing balances' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END
$$;


--
-- Name: nexora_create_default_ai_policy(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_create_default_ai_policy() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    INSERT INTO public."AiProcessingPolicies"
        ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
         "Version", "UpdatedOn", "UpdatedBy")
    VALUES (NEW."ID", TRUE, FALSE, 'RfqExtraction,BoqDraft', 1, now(), 'tenant-provisioning')
    ON CONFLICT ("BusinessUnitId") DO NOTHING;
    RETURN NEW;
END
$$;


--
-- Name: nexora_evidence_append_only(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_evidence_append_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION '% is immutable evidence', TG_TABLE_NAME USING ERRCODE = '55000';
END; $$;


--
-- Name: nexora_evidence_occurrence_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_evidence_occurrence_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'source occurrence is immutable evidence' USING ERRCODE = '55000';
    END IF;
    IF (NEW.business_unit_id, NEW.id, NEW.source_document_id, NEW.corpus_id,
        NEW.idempotency_key, NEW.source_metadata, NEW.received_on)
       IS DISTINCT FROM
       (OLD.business_unit_id, OLD.id, OLD.source_document_id, OLD.corpus_id,
        OLD.idempotency_key, OLD.source_metadata, OLD.received_on)
       OR (OLD.extraction_job_id IS NOT NULL AND NEW.extraction_job_id IS DISTINCT FROM OLD.extraction_job_id)
       OR (OLD.logical_group_key IS NOT NULL AND NEW.logical_group_key IS DISTINCT FROM OLD.logical_group_key) THEN
        RAISE EXCEPTION 'source occurrence provenance is immutable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_extraction_run_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_extraction_run_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'extraction runs cannot be deleted' USING ERRCODE = '55000';
    END IF;

    IF (NEW.id, NEW.business_unit_id, NEW.source_document_id, NEW.run_id,
        NEW.extraction_job_id, NEW.attempt_number, NEW.parser_version,
        NEW.schema_version, NEW.created_on)
       IS DISTINCT FROM
       (OLD.id, OLD.business_unit_id, OLD.source_document_id, OLD.run_id,
        OLD.extraction_job_id, OLD.attempt_number, OLD.parser_version,
        OLD.schema_version, OLD.created_on) THEN
        RAISE EXCEPTION 'extraction run identity and versions are immutable'
            USING ERRCODE = '55000';
    END IF;

    IF NOT (
        (OLD.status = 'Pending' AND NEW.status IN ('Processing', 'Failed'))
        OR (OLD.status = 'Processing' AND NEW.status IN ('Completed', 'Failed'))
    ) THEN
        RAISE EXCEPTION 'illegal or repeated extraction run transition % -> %',
            OLD.status, NEW.status USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END; $$;


--
-- Name: nexora_finance_audit_append_only(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_finance_audit_append_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'commercial finance audit records are append-only' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_finance_outbox_core_immutable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_finance_outbox_core_immutable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' OR
       (NEW."BusinessUnitId", NEW."EventId", NEW."AggregateType", NEW."AggregateId",
        NEW."AggregateVersion", NEW."EventType", NEW."Payload", NEW."SchemaVersion", NEW."OccurredOn")
       IS DISTINCT FROM
       (OLD."BusinessUnitId", OLD."EventId", OLD."AggregateType", OLD."AggregateId",
        OLD."AggregateVersion", OLD."EventType", OLD."Payload", OLD."SchemaVersion", OLD."OccurredOn") THEN
        RAISE EXCEPTION 'finance outbox event identity and payload are immutable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_finance_reject_truncate(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_finance_reject_truncate() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'governed finance tables cannot be truncated' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_gl_authenticated_actor(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_authenticated_actor(business_unit_id bigint) RETURNS text
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE actor_id text; DECLARE actor_secret text; DECLARE issued_at bigint;
DECLARE expires_at bigint; DECLARE nonce_value uuid; DECLARE envelope_signature text;
DECLARE expected_signature text; DECLARE inserted_count integer;
BEGIN
    actor_id := NULLIF(current_setting('nexora.actor_id', true), '');
    issued_at := NULLIF(current_setting('nexora.gl_issued_at', true), '')::bigint;
    expires_at := NULLIF(current_setting('nexora.gl_expires_at', true), '')::bigint;
    nonce_value := NULLIF(current_setting('nexora.gl_nonce', true), '')::uuid;
    envelope_signature := NULLIF(current_setting('nexora.gl_signature', true), '');
    SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
    IF actor_id IS NULL OR actor_secret IS NULL OR issued_at IS NULL OR expires_at IS NULL
       OR nonce_value IS NULL OR envelope_signature IS NULL OR expires_at - issued_at > 60
       OR issued_at > extract(epoch FROM clock_timestamp())::bigint + 5
       OR expires_at < extract(epoch FROM clock_timestamp())::bigint THEN
        RAISE EXCEPTION 'a current signed ledger actor envelope is required' USING ERRCODE = '42501';
    END IF;
    expected_signature := encode(hmac(convert_to(business_unit_id::text || E'\n' || actor_id || E'\n'
        || issued_at::text || E'\n' || expires_at::text || E'\n' || nonce_value::text, 'UTF8'),
        convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex');
    IF envelope_signature <> expected_signature THEN
        RAISE EXCEPTION 'the signed ledger actor envelope is invalid' USING ERRCODE = '42501';
    END IF;
    DELETE FROM public."LedgerActorNonces" WHERE "ExpiresOn" < clock_timestamp() - interval '5 minutes';
    INSERT INTO public."LedgerActorNonces" ("Nonce","BusinessUnitId","Actor","TransactionId","ExpiresOn")
    VALUES (nonce_value,business_unit_id,actor_id,txid_current(),to_timestamp(expires_at))
    ON CONFLICT ("Nonce") DO NOTHING;
    GET DIAGNOSTICS inserted_count = ROW_COUNT;
    IF inserted_count = 0 AND NOT EXISTS (SELECT 1 FROM public."LedgerActorNonces"
        WHERE "Nonce" = nonce_value AND "BusinessUnitId" = business_unit_id AND "Actor" = actor_id
          AND "TransactionId" = txid_current()) THEN
        RAISE EXCEPTION 'the signed ledger actor envelope was already consumed' USING ERRCODE = '42501';
    END IF;
    RETURN actor_id;
END
$$;


--
-- Name: nexora_gl_certify_period_close(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_certify_period_close() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE book_currency bigint;
DECLARE computed_debit numeric(18,2);
DECLARE computed_credit numeric(18,2);
DECLARE computed_count integer;
DECLARE canonical text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."CloseReason" IS NOT NULL OR NEW."CloseTrialBalanceHash" IS NOT NULL THEN
            RAISE EXCEPTION 'accounting periods cannot begin with close evidence' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'SoftClosed' AND NEW."Status" = 'Closed' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text, 0));
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
            || ':' || NEW."Id"::text, 0));
        IF length(trim(NEW."CloseReason")) < 20 OR length(trim(NEW."CloseEvidenceReference")) < 8 THEN
            RAISE EXCEPTION 'hard close requires reason and evidence' USING ERRCODE = '23514';
        END IF;
        SELECT b."FunctionalCurrencyId" INTO STRICT book_currency FROM public."LedgerBooks" b
            WHERE b."BusinessUnitId" = NEW."BusinessUnitId";
        SELECT COALESCE(sum(j."TotalDebit"),0), COALESCE(sum(j."TotalCredit"),0), count(*)
        INTO computed_debit, computed_credit, computed_count
        FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
          AND j."FunctionalCurrencyId" = book_currency AND j."AccountingDate" <= NEW."EndsOn"
          AND j."Status" IN ('Posted','Reversed');
        IF computed_debit <> computed_credit THEN
            RAISE EXCEPTION 'ledger totals do not balance for hard close' USING ERRCODE = '23514';
        END IF;
        SELECT COALESCE(string_agg(balance."LedgerAccountId"::text || ':'
            || to_char(balance.debit, 'FM9999999999999990.00') || ':'
            || to_char(balance.credit, 'FM9999999999999990.00'), '|' ORDER BY balance."LedgerAccountId"), '')
        INTO canonical FROM (
            SELECT line."LedgerAccountId", sum(line."FunctionalDebit") AS debit,
                sum(line."FunctionalCredit") AS credit
            FROM public."JournalEntryLines" line JOIN public."JournalEntries" journal
              ON journal."BusinessUnitId" = line."BusinessUnitId" AND journal."Id" = line."JournalEntryId"
            WHERE journal."BusinessUnitId" = NEW."BusinessUnitId"
              AND journal."FunctionalCurrencyId" = book_currency
              AND journal."AccountingDate" <= NEW."EndsOn" AND journal."Status" IN ('Posted','Reversed')
            GROUP BY line."LedgerAccountId") balance;
        NEW."CloseTotalDebit" := computed_debit;
        NEW."CloseTotalCredit" := computed_credit;
        NEW."CloseJournalCount" := computed_count;
        NEW."CloseTrialBalanceHash" := encode(digest(convert_to(canonical, 'UTF8'), 'sha256'), 'hex');
    ELSIF NEW."CloseReason" IS DISTINCT FROM OLD."CloseReason"
       OR NEW."CloseEvidenceReference" IS DISTINCT FROM OLD."CloseEvidenceReference"
       OR NEW."CloseTrialBalanceHash" IS DISTINCT FROM OLD."CloseTrialBalanceHash"
       OR NEW."CloseTotalDebit" IS DISTINCT FROM OLD."CloseTotalDebit"
       OR NEW."CloseTotalCredit" IS DISTINCT FROM OLD."CloseTotalCredit"
       OR NEW."CloseJournalCount" IS DISTINCT FROM OLD."CloseJournalCount" THEN
        RAISE EXCEPTION 'period close certification evidence is immutable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_gl_enforce_book_currency(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_enforce_book_currency() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_TABLE_NAME <> 'LedgerBooks' AND NOT EXISTS (
        SELECT 1 FROM public."LedgerBooks" b WHERE b."BusinessUnitId" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'a tenant accounting book is required' USING ERRCODE = '23514';
    END IF;
    IF TG_TABLE_NAME = 'JournalEntries' AND NOT EXISTS (
        SELECT 1 FROM public."LedgerBooks" b WHERE b."BusinessUnitId" = NEW."BusinessUnitId"
          AND b."FunctionalCurrencyId" = (to_jsonb(NEW)->>'FunctionalCurrencyId')::bigint) THEN
        RAISE EXCEPTION 'journal functional currency must match the immutable accounting book' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_gl_evidence_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_evidence_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE aggregate_type text; DECLARE aggregate_version bigint; DECLARE action_name text;
DECLARE event_name text; DECLARE actor_id text; DECLARE occurred_at timestamp without time zone;
DECLARE payload jsonb; DECLARE event_id uuid;
BEGIN
    aggregate_type := CASE TG_TABLE_NAME WHEN 'LedgerBooks' THEN 'LedgerBook'
        WHEN 'LedgerAccounts' THEN 'LedgerAccount' WHEN 'AccountingPeriods' THEN 'AccountingPeriod'
        ELSE 'JournalEntry' END;
    aggregate_version := NEW."Version";
    action_name := CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE to_jsonb(NEW)->>'Status' END;
    IF TG_TABLE_NAME = 'LedgerAccounts' AND TG_OP = 'UPDATE' THEN action_name := 'Deactivated'; END IF;
    IF TG_TABLE_NAME = 'AccountingPeriods' AND TG_OP = 'UPDATE'
       AND to_jsonb(OLD)->>'Status' = 'SoftClosed' AND to_jsonb(NEW)->>'Status' = 'Open' THEN
        action_name := 'Reopened';
    END IF;
    actor_id := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
        to_jsonb(NEW)->>'PostedBy', to_jsonb(NEW)->>'CreatedBy', 'system:ledger');
    occurred_at := clock_timestamp() AT TIME ZONE 'UTC'; payload := to_jsonb(NEW);
    event_name := 'finance.' || lower(aggregate_type) || '.' || lower(action_name);
    event_id := (substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),1,8)||'-'||
        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),9,4)||'-4'||
        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),14,3)||'-a'||
        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),18,3)||'-'||
        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),21,12))::uuid;
    INSERT INTO public."CommercialFinanceAudits" ("BusinessUnitId","AggregateType","AggregateId","Action","Actor","OccurredOn","DetailJson")
    VALUES (NEW."BusinessUnitId",aggregate_type,NEW."Id",action_name,actor_id,occurred_at,payload);
    INSERT INTO public."FinanceOutboxMessages" ("BusinessUnitId","EventId","AggregateType","AggregateId","AggregateVersion","EventType","Payload","SchemaVersion","OccurredOn","AvailableOn","AttemptCount")
    VALUES (NEW."BusinessUnitId",event_id,aggregate_type,NEW."Id",aggregate_version,event_name,payload,1,occurred_at,occurred_at,0);
    RETURN NULL;
END
$$;


--
-- Name: nexora_gl_guard_account(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_guard_account() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE actor_id text;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'ledger accounts cannot be deleted' USING ERRCODE = '55000'; END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Version" <> 1 OR NOT NEW."IsActive" OR NEW."DeactivatedBy" IS NOT NULL
           OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id) THEN
            RAISE EXCEPTION 'invalid initial ledger account state' USING ERRCODE = '55000';
        END IF;
        IF NEW."CurrencyId" IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."Currency" c WHERE c."ID" = NEW."CurrencyId"
              AND c."BusinessUnitID" = NEW."BusinessUnitId" AND c."IsActive" IS TRUE) THEN
            RAISE EXCEPTION 'ledger account currency must belong to the tenant and be active' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id" OR NEW."Code" <> OLD."Code"
       OR NEW."Name" <> OLD."Name" OR NEW."Category" <> OLD."Category" OR NEW."NormalBalance" <> OLD."NormalBalance"
       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId" OR NEW."IsControlAccount" <> OLD."IsControlAccount"
       OR NEW."IsContraAccount" <> OLD."IsContraAccount" OR NEW."AllowsManualPosting" <> OLD."AllowsManualPosting"
       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
       OR NOT OLD."IsActive" OR NEW."IsActive" OR NEW."Version" <> OLD."Version" + 1
       OR NEW."DeactivatedBy" IS NULL OR NEW."DeactivatedOn" IS NULL OR length(trim(NEW."DeactivationReason")) < 20
       OR (actor_id IS NOT NULL AND NEW."DeactivatedBy" <> actor_id) THEN
        RAISE EXCEPTION 'ledger account changes require the governed deactivation transition' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_gl_guard_book(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_guard_book() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE actor_id text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'the accounting book cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Version" <> 1 OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id)
           OR NOT EXISTS (SELECT 1 FROM public."Currency" c
                WHERE c."BusinessUnitID" = NEW."BusinessUnitId" AND c."ID" = NEW."FunctionalCurrencyId"
                  AND c."IsActive" IS TRUE AND c."IsBaseCurrency" IS TRUE) THEN
            RAISE EXCEPTION 'the accounting book requires the tenant active base currency and authenticated creator' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."ReceivablesControlAccountId" IS NOT NULL OR OLD."UnappliedCashAccountId" IS NOT NULL
       OR NEW."ReceivablesControlAccountId" IS NULL OR NEW."UnappliedCashAccountId" IS NULL
       OR NEW."ReceivablesControlAccountId" = NEW."UnappliedCashAccountId"
       OR NEW."Version" <> OLD."Version" + 1
       OR (NEW."BusinessUnitId", NEW."Id", NEW."Name", NEW."FunctionalCurrencyId", NEW."TimeZoneId",
            NEW."FiscalYearStartMonth", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
          IS DISTINCT FROM
          (OLD."BusinessUnitId", OLD."Id", OLD."Name", OLD."FunctionalCurrencyId", OLD."TimeZoneId",
            OLD."FiscalYearStartMonth", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn")
       OR NOT EXISTS (SELECT 1 FROM public."LedgerAccounts" account
            WHERE account."BusinessUnitId" = NEW."BusinessUnitId"
              AND account."Id" = NEW."ReceivablesControlAccountId" AND account."IsActive" IS TRUE
              AND account."IsControlAccount" IS TRUE AND account."Category" = 'Asset')
       OR NOT EXISTS (SELECT 1 FROM public."LedgerAccounts" account
            WHERE account."BusinessUnitId" = NEW."BusinessUnitId"
              AND account."Id" = NEW."UnappliedCashAccountId" AND account."IsActive" IS TRUE
              AND account."IsControlAccount" IS FALSE AND account."Category" = 'Liability') THEN
        RAISE EXCEPTION 'the accounting book permits only one governed receivables posting configuration' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_gl_guard_journal(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_guard_journal() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE actor_id text;
DECLARE allocated_number bigint;
DECLARE fiscal_year integer;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'journals cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
    END IF;
    IF TG_OP = 'INSERT' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
            || ':' || NEW."AccountingPeriodId"::text, 0));
        IF NEW."Status" <> 'Draft' OR NEW."EntryNumber" IS NOT NULL OR NEW."Version" <> 1
           OR (NEW."SourceType" = 'Manual' AND actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id)
           OR (NEW."SourceType" = 'JournalReversal' AND (NEW."ReversesJournalEntryId" IS NULL
               OR NEW."CreatedBy" <> 'system:journal-reversal')) THEN
            RAISE EXCEPTION 'invalid initial journal state' USING ERRCODE = '55000';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM public."AccountingPeriods" p WHERE p."BusinessUnitId" = NEW."BusinessUnitId"
            AND p."Id" = NEW."AccountingPeriodId" AND p."Status" = 'Open'
            AND NEW."AccountingDate" BETWEEN p."StartsOn" AND p."EndsOn")
           OR NOT EXISTS (SELECT 1 FROM public."Currency" c WHERE c."ID" = NEW."FunctionalCurrencyId"
            AND c."BusinessUnitID" = NEW."BusinessUnitId" AND c."IsActive" IS TRUE) THEN
            RAISE EXCEPTION 'journal period and currency must belong to the tenant' USING ERRCODE = '23514';
        END IF;
        IF NEW."SourceType" = 'JournalReversal' AND NOT EXISTS (
            SELECT 1 FROM public."JournalEntries" source
            WHERE source."BusinessUnitId" = NEW."BusinessUnitId"
              AND source."Id" = NEW."ReversesJournalEntryId" AND source."Status" = 'Posted'
              AND source."FunctionalCurrencyId" = NEW."FunctionalCurrencyId"
              AND source."EntryNumber" IS NOT DISTINCT FROM NEW."SourceReference"
              AND source."Version" = NEW."SourceVersion") THEN
            RAISE EXCEPTION 'a reversal draft requires the posted source journal identity and version' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
       OR NEW."AccountingPeriodId" <> OLD."AccountingPeriodId" OR NEW."FunctionalCurrencyId" <> OLD."FunctionalCurrencyId"
       OR NEW."AccountingDate" <> OLD."AccountingDate" OR NEW."Description" <> OLD."Description"
       OR NEW."SourceType" <> OLD."SourceType" OR NEW."SourceReference" IS DISTINCT FROM OLD."SourceReference"
       OR NEW."SourceVersion" IS DISTINCT FROM OLD."SourceVersion"
       OR NEW."TotalDebit" <> OLD."TotalDebit" OR NEW."TotalCredit" <> OLD."TotalCredit"
       OR NEW."ReversesJournalEntryId" IS DISTINCT FROM OLD."ReversesJournalEntryId"
       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
       OR NEW."Version" <> OLD."Version" + 1 THEN
        RAISE EXCEPTION 'journal accounting content is immutable' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Posted' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
            || ':' || NEW."AccountingPeriodId"::text, 0));
        IF NEW."PostedBy" IS NULL OR NEW."PostedOn" IS NULL OR NEW."PostedBy" = OLD."CreatedBy"
           OR (actor_id IS NOT NULL AND NEW."PostedBy" <> actor_id)
           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"
           OR NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
           OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
           OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference" THEN
            RAISE EXCEPTION 'journal posting requires an independent authenticated actor' USING ERRCODE = '55000';
        END IF;
        SELECT p."FiscalYear" INTO STRICT fiscal_year FROM public."AccountingPeriods" p
        WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Id" = NEW."AccountingPeriodId";
        INSERT INTO public."LegalDocumentCounters" ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
        VALUES (NEW."BusinessUnitId", 'Journal', fiscal_year, 2)
        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
        RETURNING "NextNumber" - 1 INTO allocated_number;
        NEW."EntryNumber" := 'JRN-' || fiscal_year::text || '-'
            || lpad(allocated_number::text, 8, '0');
    ELSIF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
        IF NEW."CancelledBy" IS NULL OR NEW."CancelledOn" IS NULL OR length(trim(NEW."CancellationReason")) < 20
           OR (actor_id IS NOT NULL AND NEW."CancelledBy" <> actor_id)
           OR NEW."EntryNumber" IS DISTINCT FROM OLD."EntryNumber"
           OR NEW."PostedBy" IS DISTINCT FROM OLD."PostedBy" OR NEW."PostedOn" IS DISTINCT FROM OLD."PostedOn"
           OR NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
           OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
           OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference" THEN
            RAISE EXCEPTION 'journal cancellation requires an authenticated actor and reason' USING ERRCODE = '55000';
        END IF;
    ELSIF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
        IF NEW."ReversedBy" IS NULL OR NEW."ReversedOn" IS NULL
           OR NEW."ReversedBy" IN (OLD."CreatedBy", OLD."PostedBy")
           OR length(trim(NEW."ReversalReason")) < 20 OR length(trim(NEW."ReversalEvidenceReference")) < 8
           OR (actor_id IS NOT NULL AND NEW."ReversedBy" <> actor_id)
           OR NEW."EntryNumber" IS DISTINCT FROM OLD."EntryNumber"
           OR NEW."PostedBy" IS DISTINCT FROM OLD."PostedBy" OR NEW."PostedOn" IS DISTINCT FROM OLD."PostedOn"
           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason" THEN
            RAISE EXCEPTION 'journal reversal requires an independent authenticated controller' USING ERRCODE = '55000';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported journal transition' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_gl_guard_line(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_guard_line() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE actor_id text;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'journal lines are append-only and cannot be changed or deleted' USING ERRCODE = '55000';
    END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(NEW."BusinessUnitId");
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
           AND j."Id" = NEW."JournalEntryId" AND j."Status" = 'Draft')
       OR NOT EXISTS (SELECT 1 FROM public."LedgerAccounts" a WHERE a."BusinessUnitId" = NEW."BusinessUnitId"
           AND a."Id" = NEW."LedgerAccountId" AND a."IsActive"
           AND (a."CurrencyId" IS NULL OR a."CurrencyId" = NEW."TransactionCurrencyId"))
       OR NOT EXISTS (SELECT 1 FROM public."Currency" c WHERE c."ID" = NEW."TransactionCurrencyId"
           AND c."BusinessUnitID" = NEW."BusinessUnitId" AND c."IsActive" IS TRUE) THEN
        RAISE EXCEPTION 'journal line references must be active tenant records' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_gl_guard_period(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_guard_period() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE actor_id text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'accounting periods cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
    END IF;
    IF TG_OP = 'INSERT' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text, 0));
        IF NEW."Status" <> 'Open' OR NEW."Version" <> 1
           OR NEW."SoftClosedBy" IS NOT NULL OR NEW."ClosedBy" IS NOT NULL OR NEW."ReopenedBy" IS NOT NULL
           OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id) THEN
            RAISE EXCEPTION 'invalid initial accounting period state' USING ERRCODE = '55000';
        END IF;
        IF EXISTS (SELECT 1 FROM public."AccountingPeriods" p
            WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."StartsOn" <= NEW."EndsOn"
              AND p."EndsOn" >= NEW."StartsOn") THEN
            RAISE EXCEPTION 'accounting periods cannot overlap' USING ERRCODE = '23P01';
        END IF;
        IF EXISTS (SELECT 1 FROM public."AccountingPeriods" p
            WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Status" = 'Closed'
              AND p."EndsOn" >= NEW."StartsOn") THEN
            RAISE EXCEPTION 'periods cannot be inserted before or within a certified close horizon' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
       OR NEW."FiscalYear" <> OLD."FiscalYear" OR NEW."PeriodNumber" <> OLD."PeriodNumber"
       OR NEW."Name" <> OLD."Name" OR NEW."StartsOn" <> OLD."StartsOn" OR NEW."EndsOn" <> OLD."EndsOn"
       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
       OR NEW."Version" <> OLD."Version" + 1 THEN
        RAISE EXCEPTION 'accounting period identity and dates are immutable' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" = 'Open' AND NEW."Status" = 'SoftClosed' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
            || ':' || NEW."Id"::text, 0));
        IF NEW."SoftClosedBy" IS NULL OR NEW."SoftClosedOn" IS NULL
           OR NEW."SoftClosedBy" = OLD."CreatedBy"
           OR (actor_id IS NOT NULL AND NEW."SoftClosedBy" <> actor_id)
           OR EXISTS (SELECT 1 FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
                AND j."AccountingPeriodId" = NEW."Id" AND j."Status" = 'Draft')
           OR NEW."ClosedBy" IS DISTINCT FROM OLD."ClosedBy" OR NEW."ClosedOn" IS DISTINCT FROM OLD."ClosedOn"
           OR NEW."ReopenedBy" IS DISTINCT FROM OLD."ReopenedBy" OR NEW."ReopenedOn" IS DISTINCT FROM OLD."ReopenedOn"
           OR NEW."ReopenReason" IS DISTINCT FROM OLD."ReopenReason"
           OR NEW."ReopenEvidenceReference" IS DISTINCT FROM OLD."ReopenEvidenceReference" THEN
            RAISE EXCEPTION 'period soft close requires an independent actor and no draft journals' USING ERRCODE = '55000';
        END IF;
    ELSIF OLD."Status" = 'SoftClosed' AND NEW."Status" = 'Closed' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text, 0));
        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
            || ':' || NEW."Id"::text, 0));
        IF NEW."ClosedBy" IS NULL OR NEW."ClosedOn" IS NULL OR NEW."ClosedBy" IN (OLD."CreatedBy", OLD."SoftClosedBy")
           OR (actor_id IS NOT NULL AND NEW."ClosedBy" <> actor_id)
           OR EXISTS (SELECT 1 FROM public."AccountingPeriods" p WHERE p."BusinessUnitId" = NEW."BusinessUnitId"
                AND p."EndsOn" < NEW."StartsOn" AND p."Status" <> 'Closed')
           OR EXISTS (SELECT 1 FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
                AND j."AccountingPeriodId" = NEW."Id" AND j."Status" = 'Draft')
           OR NEW."SoftClosedBy" IS DISTINCT FROM OLD."SoftClosedBy"
           OR NEW."SoftClosedOn" IS DISTINCT FROM OLD."SoftClosedOn"
           OR NEW."ReopenedBy" IS DISTINCT FROM OLD."ReopenedBy" OR NEW."ReopenedOn" IS DISTINCT FROM OLD."ReopenedOn"
           OR NEW."ReopenReason" IS DISTINCT FROM OLD."ReopenReason"
           OR NEW."ReopenEvidenceReference" IS DISTINCT FROM OLD."ReopenEvidenceReference" THEN
            RAISE EXCEPTION 'period close requires an independent controller and all preceding periods closed' USING ERRCODE = '55000';
        END IF;
    ELSIF OLD."Status" = 'SoftClosed' AND NEW."Status" = 'Open' THEN
        IF NEW."ReopenedBy" IS NULL OR NEW."ReopenedOn" IS NULL OR NEW."ReopenedBy" = OLD."SoftClosedBy"
           OR length(trim(NEW."ReopenReason")) < 20 OR length(trim(NEW."ReopenEvidenceReference")) < 8
           OR (actor_id IS NOT NULL AND NEW."ReopenedBy" <> actor_id)
           OR NEW."SoftClosedBy" IS DISTINCT FROM OLD."SoftClosedBy"
           OR NEW."SoftClosedOn" IS DISTINCT FROM OLD."SoftClosedOn"
           OR NEW."ClosedBy" IS DISTINCT FROM OLD."ClosedBy" OR NEW."ClosedOn" IS DISTINCT FROM OLD."ClosedOn" THEN
            RAISE EXCEPTION 'period reopening requires independent approval, reason and evidence' USING ERRCODE = '55000';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported accounting period transition' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_gl_validate_posting(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_gl_validate_posting() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE line_count integer;
DECLARE debit_total numeric(18,2);
DECLARE credit_total numeric(18,2);
DECLARE invalid_accounts integer;
DECLARE invalid_currency_balances integer;
DECLARE invalid_exchange_amounts integer;
DECLARE mismatch_count integer;
BEGIN
    IF NEW."Status" = 'Posted' AND OLD."Status" = 'Draft' THEN
        SELECT count(*), COALESCE(sum(l."FunctionalDebit"), 0), COALESCE(sum(l."FunctionalCredit"), 0),
               count(*) FILTER (WHERE NOT a."IsActive" OR (NEW."SourceType" = 'Manual'
                   AND (NOT a."AllowsManualPosting" OR a."IsControlAccount"))
                   OR (a."CurrencyId" IS NOT NULL AND a."CurrencyId" <> l."TransactionCurrencyId"))
        INTO line_count, debit_total, credit_total, invalid_accounts
        FROM public."JournalEntryLines" l JOIN public."LedgerAccounts" a
          ON a."BusinessUnitId" = l."BusinessUnitId" AND a."Id" = l."LedgerAccountId"
        WHERE l."BusinessUnitId" = NEW."BusinessUnitId" AND l."JournalEntryId" = NEW."Id";
        IF line_count < 2 OR debit_total <= 0 OR debit_total <> credit_total
           OR debit_total <> NEW."TotalDebit" OR credit_total <> NEW."TotalCredit" OR invalid_accounts > 0
           OR NOT EXISTS (SELECT 1 FROM public."AccountingPeriods" p WHERE p."BusinessUnitId" = NEW."BusinessUnitId"
                AND p."Id" = NEW."AccountingPeriodId" AND p."Status" = 'Open'
                AND NEW."AccountingDate" BETWEEN p."StartsOn" AND p."EndsOn")
           OR NOT EXISTS (SELECT 1 FROM public."Currency" c
                WHERE c."BusinessUnitID" = NEW."BusinessUnitId"
                  AND c."ID" = NEW."FunctionalCurrencyId" AND c."IsActive" IS TRUE) THEN
            RAISE EXCEPTION 'journal posting failed balance, account, or open-period controls' USING ERRCODE = '23514';
        END IF;
        SELECT count(*) INTO invalid_currency_balances FROM (
            SELECT l."TransactionCurrencyId"
            FROM public."JournalEntryLines" l
            WHERE l."BusinessUnitId" = NEW."BusinessUnitId" AND l."JournalEntryId" = NEW."Id"
            GROUP BY l."TransactionCurrencyId"
            HAVING sum(l."TransactionDebit") <> sum(l."TransactionCredit")) currency_imbalance;
        SELECT count(*) INTO invalid_exchange_amounts
        FROM public."JournalEntryLines" l
        WHERE l."BusinessUnitId" = NEW."BusinessUnitId" AND l."JournalEntryId" = NEW."Id"
          AND (round(l."TransactionDebit" * l."ExchangeRate", 2) <> l."FunctionalDebit"
            OR round(l."TransactionCredit" * l."ExchangeRate", 2) <> l."FunctionalCredit"
            OR (l."TransactionCurrencyId" = NEW."FunctionalCurrencyId" AND l."ExchangeRate" <> 1)
            OR NOT EXISTS (SELECT 1 FROM public."Currency" c
                WHERE c."BusinessUnitID" = l."BusinessUnitId" AND c."ID" = l."TransactionCurrencyId"
                  AND c."IsActive" IS TRUE));
        IF invalid_currency_balances > 0 OR invalid_exchange_amounts > 0 THEN
            RAISE EXCEPTION 'journal transaction currencies and snapshotted exchange amounts must reconcile' USING ERRCODE = '23514';
        END IF;
        IF NEW."ReversesJournalEntryId" IS NOT NULL THEN
            SELECT count(*) INTO mismatch_count FROM (
                (SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                    "TransactionCredit" AS debit, "TransactionDebit" AS credit,
                    "FunctionalCredit" AS fdebit, "FunctionalDebit" AS fcredit
                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                   AND "JournalEntryId" = NEW."ReversesJournalEntryId"
                 EXCEPT
                 SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                    "TransactionDebit", "TransactionCredit", "FunctionalDebit", "FunctionalCredit"
                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                   AND "JournalEntryId" = NEW."Id")
                UNION ALL
                (SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                    "TransactionDebit", "TransactionCredit", "FunctionalDebit", "FunctionalCredit"
                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                   AND "JournalEntryId" = NEW."Id"
                 EXCEPT
                 SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                    "TransactionCredit", "TransactionDebit", "FunctionalCredit", "FunctionalDebit"
                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                   AND "JournalEntryId" = NEW."ReversesJournalEntryId")) differences;
            IF mismatch_count > 0 THEN
                RAISE EXCEPTION 'reversal journal must exactly negate every original line' USING ERRCODE = '23514';
            END IF;
        END IF;
    ELSIF NEW."Status" = 'Reversed' AND OLD."Status" = 'Posted' THEN
        IF NOT EXISTS (SELECT 1 FROM public."JournalEntries" r WHERE r."BusinessUnitId" = NEW."BusinessUnitId"
            AND r."ReversesJournalEntryId" = NEW."Id" AND r."Status" = 'Posted') THEN
            RAISE EXCEPTION 'a posted exact reversal is required before marking a journal reversed' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END
$$;


--
-- Name: nexora_guard_ai_request_update(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_guard_ai_request_update() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF OLD."CompletedOn" IS NOT NULL THEN
        RAISE EXCEPTION 'Completed AI requests are immutable' USING ERRCODE = '55000';
    END IF;
    IF ROW(NEW."BusinessUnitId", NEW."ExtractionJobId", NEW."SourceDocumentOccurrenceId",
           NEW."Operation", NEW."IdempotencyKey", NEW."PromptHash", NEW."PromptVersion",
           NEW."Provider", NEW."ProviderClass", NEW."Model", NEW."InputCharacters",
           NEW."InputHash", NEW."InjectionDetected", NEW."EstimatedInputTokens",
           NEW."ReservedTokens", NEW."BudgetWarning", NEW."CreatedOn")
       IS DISTINCT FROM
       ROW(OLD."BusinessUnitId", OLD."ExtractionJobId", OLD."SourceDocumentOccurrenceId",
           OLD."Operation", OLD."IdempotencyKey", OLD."PromptHash", OLD."PromptVersion",
           OLD."Provider", OLD."ProviderClass", OLD."Model", OLD."InputCharacters",
           OLD."InputHash", OLD."InjectionDetected", OLD."EstimatedInputTokens",
           OLD."ReservedTokens", OLD."BudgetWarning", OLD."CreatedOn") THEN
        RAISE EXCEPTION 'AI request identity, linkage and reservation fields are immutable' USING ERRCODE = '55000';
    END IF;
    IF (OLD."Status" = 'Reserved' AND NEW."Status" NOT IN ('Reserved', 'Running', 'Succeeded', 'Failed', 'Unknown'))
       OR (OLD."Status" = 'Running' AND NEW."Status" NOT IN ('Running', 'Succeeded', 'Failed', 'Unknown'))
       OR OLD."Status" IN ('Succeeded', 'Denied', 'Failed', 'Unknown') THEN
        RAISE EXCEPTION 'Invalid AI request status transition' USING ERRCODE = '55000';
    END IF;
    IF NEW."Status" IN ('Reserved', 'Running')
       AND ROW(NEW."OutputCharacters", NEW."OutputHash", NEW."InputTokens", NEW."OutputTokens",
               NEW."TokenSource", NEW."EstimatedCost", NEW."CostCurrency", NEW."CostStatus",
               NEW."CostPricingVersion", NEW."ErrorCode", NEW."CompletedOn")
           IS DISTINCT FROM
           ROW(OLD."OutputCharacters", OLD."OutputHash", OLD."InputTokens", OLD."OutputTokens",
               OLD."TokenSource", OLD."EstimatedCost", OLD."CostCurrency", OLD."CostStatus",
               OLD."CostPricingVersion", OLD."ErrorCode", OLD."CompletedOn") THEN
        RAISE EXCEPTION 'AI usage and cost can only be finalized with a terminal state' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_guard_commercial_exception_case(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_exception_case() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (
        NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId" OR
        NEW."CommercialCaseId" IS DISTINCT FROM OLD."CommercialCaseId" OR
        NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial" OR
        NEW."ExceptionType" IS DISTINCT FROM OLD."ExceptionType" OR
        NEW."ExceptionKey" IS DISTINCT FROM OLD."ExceptionKey" OR
        NEW."SourceType" IS DISTINCT FROM OLD."SourceType" OR
        NEW."SourceId" IS DISTINCT FROM OLD."SourceId" OR
        NEW."FollowUpTaskId" IS DISTINCT FROM OLD."FollowUpTaskId" OR
        NEW."UnassignedWorkItemId" IS DISTINCT FROM OLD."UnassignedWorkItemId" OR
        NEW."FirstDetectedAtUtc" IS DISTINCT FROM OLD."FirstDetectedAtUtc"
    ) THEN
        RAISE EXCEPTION 'commercial exception lineage is immutable';
    END IF;

    IF NEW."OwnerUserId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM public."Users" u
        WHERE u."ID" = NEW."OwnerUserId" AND u."BUID" = NEW."BusinessUnitId"
    ) THEN
        RAISE EXCEPTION 'commercial exception owner must belong to the same tenant';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_guard_commercial_exception_outbox(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_exception_outbox() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' OR
       NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId" OR
       NEW."CommercialExceptionEventId" IS DISTINCT FROM OLD."CommercialExceptionEventId" OR
       NEW."EventType" IS DISTINCT FROM OLD."EventType" OR
       NEW."Payload" IS DISTINCT FROM OLD."Payload" OR
       NEW."OccurredAtUtc" IS DISTINCT FROM OLD."OccurredAtUtc" THEN
        RAISE EXCEPTION 'commercial exception outbox identity and payload are immutable';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_guard_commercial_line_resolution_update(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_guard_commercial_line_resolution_update() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."RfqId" IS NOT NULL AND OLD."RfqId" IS NULL
       AND NEW."RfqItemId" IS NOT NULL AND OLD."RfqItemId" IS NULL
       AND NEW."BusinessUnitId" = OLD."BusinessUnitId"
       AND NEW."LeadId" = OLD."LeadId"
       AND NEW."LeadRevisionId" = OLD."LeadRevisionId"
       AND NEW."LeadLineId" = OLD."LeadLineId"
       AND NEW."ProductId" IS NOT DISTINCT FROM OLD."ProductId"
       AND NEW."RequestedPartNumber" = OLD."RequestedPartNumber"
       AND NEW."RequestedQuantity" = OLD."RequestedQuantity"
       AND NEW."Classification" = OLD."Classification"
       AND NEW."AvailableToPromise" = OLD."AvailableToPromise"
       AND NEW."IncomingAvailable" = OLD."IncomingAvailable"
       AND NEW."ProjectedShortage" = OLD."ProjectedShortage"
       AND NEW."LeadTimeDays" IS NOT DISTINCT FROM OLD."LeadTimeDays"
       AND NEW."ExpectedAvailableOn" IS NOT DISTINCT FROM OLD."ExpectedAvailableOn"
       AND NEW."UnitCost" IS NOT DISTINCT FROM OLD."UnitCost"
       AND NEW."CostCurrencyCode" IS NOT DISTINCT FROM OLD."CostCurrencyCode"
       AND NEW."FulfilmentJson" = OLD."FulfilmentJson"
       AND NEW."RelatedResourcesJson" = OLD."RelatedResourcesJson"
       AND NEW."ProductResolutionJson" = OLD."ProductResolutionJson"
       AND NEW."ResolutionMethod" = OLD."ResolutionMethod"
       AND NEW."EvidenceReference" IS NOT DISTINCT FROM OLD."EvidenceReference"
       AND NEW."InventoryAsOfUtc" = OLD."InventoryAsOfUtc"
       AND NEW."ResolvedOn" = OLD."ResolvedOn" THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'commercial line resolutions are immutable';
END $$;


--
-- Name: nexora_guard_opportunity_outbox(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_guard_opportunity_outbox() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' OR
       NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId" OR
       NEW."OpportunityEventId" IS DISTINCT FROM OLD."OpportunityEventId" OR
       NEW."EventType" IS DISTINCT FROM OLD."EventType" OR
       NEW."PayloadJson" IS DISTINCT FROM OLD."PayloadJson" OR
       NEW."OccurredAtUtc" IS DISTINCT FROM OLD."OccurredAtUtc" THEN
        RAISE EXCEPTION 'commercial opportunity outbox identity and payload are immutable';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_guard_quote_delivery_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_guard_quote_delivery_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'quote delivery requests cannot be deleted';
    END IF;
    IF OLD."CompletedOn" IS NOT NULL OR OLD."DeadLetteredOn" IS NOT NULL THEN
        RAISE EXCEPTION 'terminal quote delivery requests are immutable';
    END IF;
    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId"
       OR NEW."QuoteId" <> OLD."QuoteId"
       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey"
       OR NEW."RecipientEmail" <> OLD."RecipientEmail"
       OR NEW."Subject" <> OLD."Subject"
       OR NEW."Body" <> OLD."Body"
       OR NEW."FromEmail" IS DISTINCT FROM OLD."FromEmail"
       OR NEW."AttachmentFileName" <> OLD."AttachmentFileName"
       OR NEW."RequestedOn" <> OLD."RequestedOn"
       OR NEW."AttemptCount" < OLD."AttemptCount"
       OR NEW."Version" <= OLD."Version" THEN
        RAISE EXCEPTION 'quote delivery identity and payload are immutable';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_otc_allocation_delete_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_allocation_delete_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM public."CustomerAwards" a
               WHERE a."Id" = OLD."CustomerAwardId" AND a."BusinessUnitId" = OLD."BusinessUnitId"
                 AND a."Status" <> 'DRAFT') THEN
        RAISE EXCEPTION 'allocations of finalized awards are immutable' USING ERRCODE = '55000';
    END IF;
    RETURN OLD;
END
$$;


--
-- Name: nexora_otc_audit_append_only(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_audit_append_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'order-to-cash audit events are append-only' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_otc_award_transition_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_award_transition_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE exceeded record;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD."Status" <> 'DRAFT' THEN
            RAISE EXCEPTION 'finalized awards are immutable' USING ERRCODE = '55000';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."Status" IN ('CONFIRMED', 'ORDERED') THEN
        IF (NEW."BusinessUnitId", NEW."AwardNumber", NEW."CustomerPurchaseOrderId", NEW."QuoteId",
            NEW."CommercialCaseId", NEW."CustomerId", NEW."CurrencyId", NEW."ConfirmedOn", NEW."ConfirmedBy")
           IS DISTINCT FROM
           (OLD."BusinessUnitId", OLD."AwardNumber", OLD."CustomerPurchaseOrderId", OLD."QuoteId",
            OLD."CommercialCaseId", OLD."CustomerId", OLD."CurrencyId", OLD."ConfirmedOn", OLD."ConfirmedBy") THEN
            RAISE EXCEPTION 'confirmed award identity and confirmation evidence are immutable' USING ERRCODE = '55000';
        END IF;
        IF OLD."Status" = 'ORDERED' OR NEW."Status" NOT IN ('ORDERED', 'CANCELLED') THEN
            RAISE EXCEPTION 'invalid finalized award transition' USING ERRCODE = '55000';
        END IF;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."Status" = 'DRAFT' AND NEW."Status" = 'CONFIRMED' THEN
        IF NOT EXISTS (SELECT 1 FROM public."CustomerAwardLineAllocations" x
                       WHERE x."BusinessUnitId" = NEW."BusinessUnitId" AND x."CustomerAwardId" = NEW."Id") THEN
            RAISE EXCEPTION 'an award requires at least one allocation before confirmation' USING ERRCODE = '23514';
        END IF;

        PERFORM l."Id" FROM public."CustomerPurchaseOrderLines" l
        JOIN public."CustomerAwardLineAllocations" x
          ON x."BusinessUnitId" = l."BusinessUnitId" AND x."CustomerPurchaseOrderLineId" = l."Id"
        WHERE x."BusinessUnitId" = NEW."BusinessUnitId" AND x."CustomerAwardId" = NEW."Id"
        ORDER BY l."Id" FOR UPDATE OF l;
        PERFORM qi."ID" FROM public."QuoteItems" qi
        JOIN public."CustomerAwardLineAllocations" x ON x."QuoteItemId" = qi."ID"
        WHERE x."BusinessUnitId" = NEW."BusinessUnitId" AND x."CustomerAwardId" = NEW."Id"
        ORDER BY qi."ID" FOR UPDATE OF qi;

        SELECT capacity."Kind", capacity."LineId", capacity."Allocated", capacity."Capacity"
        INTO exceeded
        FROM (
            SELECT 'PO'::text AS "Kind", l."Id" AS "LineId", l."OrderedQuantity" AS "Capacity",
                   sum(x."AwardedQuantity") AS "Allocated"
            FROM public."CustomerPurchaseOrderLines" l
            JOIN public."CustomerAwardLineAllocations" x
              ON x."BusinessUnitId" = l."BusinessUnitId" AND x."CustomerPurchaseOrderLineId" = l."Id"
            JOIN public."CustomerAwards" a
              ON a."BusinessUnitId" = x."BusinessUnitId" AND a."Id" = x."CustomerAwardId"
            WHERE l."BusinessUnitId" = NEW."BusinessUnitId"
              AND (a."Status" IN ('CONFIRMED','ORDERED') OR a."Id" = NEW."Id")
            GROUP BY l."Id", l."OrderedQuantity"
            HAVING sum(x."AwardedQuantity") > l."OrderedQuantity"
            UNION ALL
            SELECT 'QUOTE', qi."ID", qi."Quantity", sum(x."AwardedQuantity")
            FROM public."QuoteItems" qi
            JOIN public."CustomerAwardLineAllocations" x ON x."QuoteItemId" = qi."ID"
            JOIN public."CustomerAwards" a
              ON a."BusinessUnitId" = x."BusinessUnitId" AND a."Id" = x."CustomerAwardId"
            WHERE a."BusinessUnitId" = NEW."BusinessUnitId"
              AND (a."Status" IN ('CONFIRMED','ORDERED') OR a."Id" = NEW."Id")
            GROUP BY qi."ID", qi."Quantity"
            HAVING sum(x."AwardedQuantity") > qi."Quantity"
        ) capacity
        LIMIT 1;
        IF FOUND THEN
            RAISE EXCEPTION '% line % allocation % exceeds capacity %',
                exceeded."Kind", exceeded."LineId", exceeded."Allocated", exceeded."Capacity"
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_order_item_source_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_order_item_source_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE source_record record;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD."CustomerAwardLineAllocationID" IS NOT NULL THEN
            RAISE EXCEPTION 'award-derived order items and their source links are immutable' USING ERRCODE = '55000';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW."CustomerAwardLineAllocationID"
       IS DISTINCT FROM OLD."CustomerAwardLineAllocationID" THEN
        RAISE EXCEPTION 'order-item award source link is immutable' USING ERRCODE = '55000';
    END IF;
    IF NEW."CustomerAwardLineAllocationID" IS NOT NULL THEN
        SELECT o."CustomerAwardID", x."CustomerAwardId", qi."ProductID"
        INTO source_record
        FROM public."Orders" o
        JOIN public."CustomerAwardLineAllocations" x ON x."Id" = NEW."CustomerAwardLineAllocationID"
        JOIN public."QuoteItems" qi ON qi."ID" = x."QuoteItemId"
        WHERE o."ID" = NEW."OrderID" AND o."BusinessUnitID" = x."BusinessUnitId";
        IF NOT FOUND OR source_record."CustomerAwardID" IS DISTINCT FROM source_record."CustomerAwardId"
           OR NEW."ProductID" IS DISTINCT FROM source_record."ProductID" THEN
            RAISE EXCEPTION 'order item does not match its award allocation' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_order_source_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_order_source_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE award_record record;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD."CustomerAwardID" IS NOT NULL THEN
            RAISE EXCEPTION 'award-derived orders and their source links are immutable' USING ERRCODE = '55000';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW."BusinessUnitID", NEW."SourceType", NEW."CustomerAwardID", NEW."QuoteID")
       IS DISTINCT FROM (OLD."BusinessUnitID", OLD."SourceType", OLD."CustomerAwardID", OLD."QuoteID") THEN
        RAISE EXCEPTION 'order source links are immutable' USING ERRCODE = '55000';
    END IF;
    IF NEW."CustomerAwardID" IS NOT NULL THEN
        SELECT a."QuoteId", a."CustomerId", a."CurrencyId", a."Status" INTO award_record
        FROM public."CustomerAwards" a
        WHERE a."Id" = NEW."CustomerAwardID" AND a."BusinessUnitId" = NEW."BusinessUnitID";
        IF NOT FOUND OR award_record."Status" NOT IN ('CONFIRMED','ORDERED')
           OR NEW."QuoteID" IS DISTINCT FROM award_record."QuoteId"
           OR NEW."CustomerID" IS DISTINCT FROM award_record."CustomerId"
           OR NEW."CurrencyID" IS DISTINCT FROM award_record."CurrencyId" THEN
            RAISE EXCEPTION 'order does not match its confirmed customer award' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_outbox_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_outbox_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE event_type text;
DECLARE event_time timestamp without time zone := (CURRENT_TIMESTAMP AT TIME ZONE 'UTC');
BEGIN
    IF TG_TABLE_NAME = 'CustomerPurchaseOrders' THEN
        IF TG_OP = 'INSERT' THEN
            event_type := 'order-to-cash.customer-po.created';
        ELSIF OLD."Status" IS DISTINCT FROM NEW."Status" THEN
            event_type := 'order-to-cash.customer-po.' || lower(replace(NEW."Status", '_', '-'));
        ELSE
            RETURN NEW;
        END IF;
        PERFORM public.nexora_write_finance_outbox(
            NEW."BusinessUnitId", 'CustomerPurchaseOrder', NEW."Id", NEW."Version", event_type,
            jsonb_build_object('Id', NEW."Id", 'InternalNumber', NEW."InternalNumber",
                'ExternalPoNumber', NEW."ExternalPoNumber", 'Status', NEW."Status",
                'CustomerId', NEW."CustomerId", 'CommercialCaseId', NEW."CommercialCaseId",
                'Version', NEW."Version"), COALESCE(NEW."ModifiedOn", NEW."CreatedOn", event_time));
    ELSIF TG_TABLE_NAME = 'CustomerAwards' THEN
        IF TG_OP = 'INSERT' THEN
            event_type := 'order-to-cash.customer-award.created';
        ELSIF OLD."Status" IS DISTINCT FROM NEW."Status" THEN
            event_type := 'order-to-cash.customer-award.' || lower(NEW."Status");
        ELSE
            RETURN NEW;
        END IF;
        PERFORM public.nexora_write_finance_outbox(
            NEW."BusinessUnitId", 'CustomerAward', NEW."Id", NEW."Version", event_type,
            jsonb_build_object('Id', NEW."Id", 'AwardNumber', NEW."AwardNumber",
                'CustomerPurchaseOrderId', NEW."CustomerPurchaseOrderId", 'QuoteId', NEW."QuoteId",
                'Status', NEW."Status", 'Version', NEW."Version"),
            COALESCE(NEW."ModifiedOn", NEW."ConfirmedOn", NEW."CancelledOn", NEW."CreatedOn", event_time));
    ELSIF TG_TABLE_NAME = 'Orders' AND TG_OP = 'INSERT'
          AND NEW."SourceType" = 'CUSTOMER_AWARD' THEN
        PERFORM public.nexora_write_finance_outbox(
            NEW."BusinessUnitID", 'Order', NEW."ID", 1, 'order-to-cash.customer-award.converted',
            jsonb_build_object('Id', NEW."ID", 'OrderNo', NEW."OrderNo",
                'CustomerAwardId', NEW."CustomerAwardID", 'QuoteId', NEW."QuoteID",
                'SourceType', NEW."SourceType"), COALESCE(NEW."CreatedOn", event_time));
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_validate_allocation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_validate_allocation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE award_record record;
DECLARE po_line_record record;
DECLARE quote_item_record record;
BEGIN
    SELECT a."CustomerPurchaseOrderId", a."QuoteId", a."Status"
    INTO award_record FROM public."CustomerAwards" a
    WHERE a."Id" = NEW."CustomerAwardId" AND a."BusinessUnitId" = NEW."BusinessUnitId"
    FOR UPDATE;
    SELECT l."CustomerPurchaseOrderId", l."ProductId"
    INTO po_line_record FROM public."CustomerPurchaseOrderLines" l
    WHERE l."Id" = NEW."CustomerPurchaseOrderLineId" AND l."BusinessUnitId" = NEW."BusinessUnitId";
    SELECT qi."QuoteID", qi."ProductID"
    INTO quote_item_record FROM public."QuoteItems" qi
    JOIN public."Quotes" q ON q."ID" = qi."QuoteID"
    WHERE qi."ID" = NEW."QuoteItemId" AND q."BusinessUnitID" = NEW."BusinessUnitId";
    IF award_record IS NULL OR po_line_record IS NULL OR quote_item_record IS NULL THEN
        RAISE EXCEPTION 'allocation references do not belong to the award tenant' USING ERRCODE = '23503';
    END IF;
    IF award_record."CustomerPurchaseOrderId" <> po_line_record."CustomerPurchaseOrderId"
       OR award_record."QuoteId" <> quote_item_record."QuoteID" THEN
        RAISE EXCEPTION 'allocation crosses its award purchase order or quote' USING ERRCODE = '23514';
    END IF;
    IF po_line_record."ProductId" IS NOT NULL
       AND quote_item_record."ProductID" IS DISTINCT FROM po_line_record."ProductId" THEN
        RAISE EXCEPTION 'allocation product does not match the PO and quote lines' USING ERRCODE = '23514';
    END IF;
    IF award_record."Status" <> 'DRAFT' THEN
        RAISE EXCEPTION 'allocations of finalized awards are immutable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_validate_award(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_validate_award() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE po_record record;
DECLARE quote_record record;
BEGIN
    SELECT p."CommercialCaseId", p."CustomerId", p."CurrencyId", p."Status"
    INTO po_record
    FROM public."CustomerPurchaseOrders" p
    WHERE p."Id" = NEW."CustomerPurchaseOrderId"
      AND p."BusinessUnitId" = NEW."BusinessUnitId";
    IF NOT FOUND OR po_record."CommercialCaseId" <> NEW."CommercialCaseId"
       OR po_record."CustomerId" <> NEW."CustomerId"
       OR po_record."CurrencyId" <> NEW."CurrencyId" THEN
        RAISE EXCEPTION 'award identity does not match its customer purchase order' USING ERRCODE = '23514';
    END IF;
    IF po_record."Status" IN ('CLOSED', 'CANCELLED') THEN
        RAISE EXCEPTION 'awards cannot be attached to a closed or cancelled purchase order' USING ERRCODE = '23514';
    END IF;
    SELECT l."CommercialCaseId", q."CustomerID", q."CurrencyID"
    INTO quote_record
    FROM public."Quotes" q
    JOIN public."RFQ" r ON r."ID" = q."RFQID" AND r."BusinessUnitID" = q."BusinessUnitID"
    JOIN public."Leads" l ON l."ID" = r."LeadID" AND l."BusinessUnitID" = q."BusinessUnitID"
    WHERE q."ID" = NEW."QuoteId" AND q."BusinessUnitID" = NEW."BusinessUnitId";
    IF NOT FOUND OR quote_record."CommercialCaseId" <> NEW."CommercialCaseId"
       OR quote_record."CustomerID" <> NEW."CustomerId"
       OR quote_record."CurrencyID" <> NEW."CurrencyId" THEN
        RAISE EXCEPTION 'award identity does not match its quote' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_validate_purchase_order(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_validate_purchase_order() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW."ExternalPoNumber" := btrim(NEW."ExternalPoNumber");
    NEW."NormalizedExternalPoNumber" := upper(regexp_replace(NEW."ExternalPoNumber", '[[:space:]]+', ' ', 'g'));
    IF NEW."NormalizedExternalPoNumber" = '' THEN
        RAISE EXCEPTION 'external customer PO number must contain letters or digits' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public."Customers" c
                   WHERE c."ID" = NEW."CustomerId" AND c."BUID" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'customer does not belong to the purchase-order tenant' USING ERRCODE = '23503';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM public."Leads" l
        JOIN public."RFQ" r ON r."LeadID" = l."ID"
          AND r."BusinessUnitID" = l."BusinessUnitID"
        WHERE l."CommercialCaseId" = NEW."CommercialCaseId"
          AND l."BusinessUnitID" = NEW."BusinessUnitId"
          AND r."CustomerID" = NEW."CustomerId") THEN
        RAISE EXCEPTION 'purchase-order customer does not match the commercial case' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_otc_validate_purchase_order_line(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_otc_validate_purchase_order_line() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM public."Products" p
        WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'product does not belong to the purchase-order tenant' USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_payment_allocation_valid(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_payment_allocation_valid() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE payment_row record;
DECLARE document_row record;
DECLARE payment_allocated numeric(18,2);
DECLARE refund_reserved numeric(18,2);
DECLARE document_allocated numeric(18,2);
DECLARE issued_credits numeric(18,2);
DECLARE posted_write_offs numeric(18,2);
DECLARE document_outstanding numeric(18,2);
BEGIN
    SELECT document.* INTO document_row
    FROM public."ReceivableDocuments" document
    WHERE document."Id" = NEW."ReceivableDocumentId"
      AND document."BusinessUnitId" = NEW."BusinessUnitId" FOR UPDATE;
    IF NOT FOUND OR document_row."Status" <> 'Issued'
       OR document_row."DocumentType" NOT IN ('Invoice', 'DebitNote') THEN
        RAISE EXCEPTION 'payments require a same-tenant issued invoice or debit note' USING ERRCODE = '23514';
    END IF;
    SELECT payment.* INTO payment_row
    FROM public."CustomerPayments" payment
    WHERE payment."Id" = NEW."CustomerPaymentId"
      AND payment."BusinessUnitId" = NEW."BusinessUnitId" FOR UPDATE;
    IF NOT FOUND OR payment_row."Status" <> 'Posted' THEN
        RAISE EXCEPTION 'payment allocation parent is invalid' USING ERRCODE = '23503';
    END IF;
    IF (payment_row."CustomerId", payment_row."CurrencyId") IS DISTINCT FROM
       (document_row."CustomerId", document_row."CurrencyId") THEN
        RAISE EXCEPTION 'payment customer and currency must match the receivable document' USING ERRCODE = '23514';
    END IF;
    SELECT coalesce(sum(allocation."Amount"), 0) INTO payment_allocated
    FROM public."PaymentAllocations" allocation
    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
      AND allocation."CustomerPaymentId" = NEW."CustomerPaymentId"
      AND allocation."Id" <> coalesce(NEW."Id", 0);
    SELECT coalesce(sum(refund."Amount"), 0) INTO refund_reserved
    FROM public."CustomerRefunds" refund
    WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
      AND refund."SourcePaymentId" = NEW."CustomerPaymentId"
      AND refund."Status" IN ('Approved', 'Released');
    IF payment_allocated + refund_reserved + NEW."Amount" > payment_row."Amount" THEN
        RAISE EXCEPTION 'payment allocations exceed the unreserved payment amount' USING ERRCODE = '23514';
    END IF;
    SELECT coalesce(sum(allocation."Amount"), 0) INTO document_allocated
    FROM public."PaymentAllocations" allocation
    JOIN public."CustomerPayments" payment
      ON payment."BusinessUnitId" = allocation."BusinessUnitId"
     AND payment."Id" = allocation."CustomerPaymentId"
    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
      AND allocation."ReceivableDocumentId" = NEW."ReceivableDocumentId"
      AND allocation."Id" <> coalesce(NEW."Id", 0) AND payment."Status" = 'Posted';
    issued_credits := 0;
    IF document_row."DocumentType" = 'Invoice' THEN
        SELECT coalesce(sum(credit."TotalAmount"), 0) INTO issued_credits
        FROM public."ReceivableDocuments" credit
        WHERE credit."BusinessUnitId" = NEW."BusinessUnitId"
          AND credit."ParentDocumentId" = NEW."ReceivableDocumentId"
          AND credit."DocumentType" = 'CreditNote' AND credit."Status" = 'Issued';
    END IF;
    SELECT coalesce(sum(allocation."Amount"), 0) INTO posted_write_offs
    FROM public."WriteOffAllocations" allocation
    JOIN public."ReceivableWriteOffs" write_off
      ON write_off."BusinessUnitId" = allocation."BusinessUnitId"
     AND write_off."Id" = allocation."ReceivableWriteOffId"
    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
      AND allocation."ReceivableDocumentId" = NEW."ReceivableDocumentId"
      AND write_off."Status" = 'Posted';
    document_outstanding := round(document_row."TotalAmount" - issued_credits
        - document_allocated - posted_write_offs, 2);
    IF NEW."Amount" > document_outstanding THEN
        RAISE EXCEPTION 'payment allocation exceeds live receivable outstanding' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_payment_outbox_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_payment_outbox_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE event_type text; DECLARE event_time timestamp without time zone; DECLARE event_actor text;
DECLARE evidence jsonb;
BEGIN
    IF TG_OP = 'INSERT' AND NEW."Status" = 'Posted' THEN
        event_type := 'finance.payment.posted';
        event_time := coalesce(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
        event_actor := NEW."CreatedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
        event_type := 'finance.payment.reversed';
        event_time := coalesce(NEW."ReversedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
        event_actor := coalesce(to_jsonb(NEW)->>'ReversedBy', NEW."CreatedBy");
    ELSE
        RETURN NEW;
    END IF;
    evidence := jsonb_build_object(
        'receiptNumber', NEW."ReceiptNumber", 'amount', NEW."Amount", 'version', NEW."Version",
        'BankAccountId', to_jsonb(NEW)->'BankAccountId',
        'JournalEntryId', to_jsonb(NEW)->'JournalEntryId',
        'ReversalJournalEntryId', to_jsonb(NEW)->'ReversalJournalEntryId');
    PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'CustomerPayment',
        NEW."Id", CASE WHEN event_type = 'finance.payment.posted' THEN 'Posted' ELSE 'Reversed' END,
        event_actor, evidence, event_time);
    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerPayment',
        NEW."Id", NEW."Version", event_type, evidence || jsonb_build_object(
            'Id', NEW."Id", 'Status', NEW."Status", 'CustomerId', NEW."CustomerId",
            'CommercialCaseId', NEW."CommercialCaseId", 'CurrencyId', NEW."CurrencyId",
            'Actor', event_actor), event_time);
    RETURN NEW;
END
$$;


--
-- Name: nexora_payment_posted_immutable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_payment_posted_immutable() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."AccountingBridgeRequired" IS NOT TRUE THEN
            RAISE EXCEPTION 'new customer payments must use the governed accounting bridge' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'posted customer payments cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF OLD."AccountingBridgeRequired" IS TRUE AND NEW."AccountingBridgeRequired" IS NOT TRUE THEN
        RAISE EXCEPTION 'the governed accounting bridge marker cannot be disabled' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Posted'
       AND OLD."JournalEntryId" IS NULL AND NEW."JournalEntryId" IS NOT NULL
       AND NEW."ReversalJournalEntryId" IS NULL
       AND NEW."Version" = OLD."Version"
       AND (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
            NEW."ReceiptNumber", NEW."PaymentDate", NEW."Amount", NEW."Method", NEW."BankReference",
            NEW."BankAccountId", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn",
            NEW."ReversedBy", NEW."ReversedOn", NEW."ReversalReason")
           IS NOT DISTINCT FROM
           (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
            OLD."ReceiptNumber", OLD."PaymentDate", OLD."Amount", OLD."Method", OLD."BankReference",
            OLD."BankAccountId", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn",
            OLD."ReversedBy", OLD."ReversedOn", OLD."ReversalReason") THEN
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed'
       AND ((OLD."JournalEntryId" IS NOT NULL AND NEW."JournalEntryId" = OLD."JournalEntryId"
              AND OLD."ReversalJournalEntryId" IS NULL AND NEW."ReversalJournalEntryId" IS NOT NULL)
            OR (OLD."JournalEntryId" IS NULL AND NEW."JournalEntryId" IS NULL
              AND OLD."ReversalJournalEntryId" IS NULL AND NEW."ReversalJournalEntryId" IS NULL))
       AND NEW."ReversedBy" IS NOT NULL
       AND lower(trim(NEW."ReversedBy")) <> lower(trim(OLD."CreatedBy"))
       AND NEW."ReversedOn" IS NOT NULL AND length(trim(NEW."ReversalReason")) > 0
       AND NEW."Version" = OLD."Version" + 1
       AND NOT EXISTS (
           SELECT 1 FROM public."CustomerRefunds" refund
           WHERE refund."BusinessUnitId" = OLD."BusinessUnitId"
             AND refund."SourcePaymentId" = OLD."Id"
             AND refund."Status" IN ('Approved', 'Released'))
       AND (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
            NEW."ReceiptNumber", NEW."PaymentDate", NEW."Amount", NEW."Method", NEW."BankReference",
            NEW."BankAccountId", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
           IS NOT DISTINCT FROM
           (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
            OLD."ReceiptNumber", OLD."PaymentDate", OLD."Amount", OLD."Method", OLD."BankReference",
            OLD."BankAccountId", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'posted customer payments are immutable or reserved by an active refund' USING ERRCODE = '55000';
END $$;


--
-- Name: nexora_protect_commercial_identity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_commercial_identity() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_TABLE_NAME = 'Leads' THEN
        IF NEW."CommercialCaseId" IS DISTINCT FROM OLD."CommercialCaseId"
           OR NEW."CommercialCaseReference" IS DISTINCT FROM OLD."CommercialCaseReference"
           OR NEW."BusinessUnitID" IS DISTINCT FROM OLD."BusinessUnitID" THEN
            RAISE EXCEPTION 'A lead commercial-case identity and tenant cannot be changed.';
        END IF;
    ELSE
        IF TG_OP = 'DELETE' OR NEW."AllocationNumber" IS DISTINCT FROM OLD."AllocationNumber"
           OR NEW."MasterReference" IS DISTINCT FROM OLD."MasterReference"
           OR NEW."BusinessUnitID" IS DISTINCT FROM OLD."BusinessUnitID" THEN
            RAISE EXCEPTION 'The permanent commercial-case identity cannot be changed or deleted.';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_protect_commercial_lifecycle_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_commercial_lifecycle_event() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING ERRCODE = '55000',
        MESSAGE = 'Commercial lifecycle events are append-only.';
END;
$$;


--
-- Name: nexora_protect_custom_field_governance(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_custom_field_governance() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'DELETE' OR TG_TABLE_NAME IN
        ('custom_field_versions', 'custom_field_options', 'custom_field_rules',
         'custom_field_dependencies', 'custom_field_value_history') THEN
        RAISE EXCEPTION 'Governed custom-field records cannot be modified or deleted.';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_protect_lead_status_history(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_lead_status_history() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Lead status history is append-only.';
END;
$$;


--
-- Name: nexora_protect_procurement_callback_receipt(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_callback_receipt() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'procurement callback receipts are append-only' USING ERRCODE = '23514';
END
$$;


--
-- Name: nexora_protect_procurement_handoff_lineage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_procurement_handoff_lineage() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Procurement handoff records are append-preserving.' USING ERRCODE = '55000';
    END IF;
    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
       OR NEW."CustomerOrderId" IS DISTINCT FROM OLD."CustomerOrderId"
       OR NEW."CustomerOrderLineId" IS DISTINCT FROM OLD."CustomerOrderLineId"
       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
       OR NEW."SourcingAwardId" IS DISTINCT FROM OLD."SourcingAwardId"
       OR NEW."SupplierQuotedItemId" IS DISTINCT FROM OLD."SupplierQuotedItemId"
       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId"
       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
       OR NEW."RequiredQuantity" IS DISTINCT FROM OLD."RequiredQuantity"
       OR NEW."SelectedUnitCost" IS DISTINCT FROM OLD."SelectedUnitCost"
       OR NEW."RequiredOn" IS DISTINCT FROM OLD."RequiredOn"
       OR NEW."DestinationType" IS DISTINCT FROM OLD."DestinationType"
       OR NEW."WarehouseId" IS DISTINCT FROM OLD."WarehouseId"
       OR NEW."DeliveryLocation" IS DISTINCT FROM OLD."DeliveryLocation"
       OR NEW."ExternalSystemTarget" IS DISTINCT FROM OLD."ExternalSystemTarget"
       OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
       OR NEW."RequestHash" IS DISTINCT FROM OLD."RequestHash" THEN
        RAISE EXCEPTION 'Procurement handoff commercial lineage is immutable.' USING ERRCODE = '55000';
    END IF;
    IF NEW."Status" IS DISTINCT FROM OLD."Status" AND NOT (
        (OLD."Status" = 'CREATED' AND NEW."Status" IN ('EXTERNAL_PO_CREATED','CANCELLED')) OR
        (OLD."Status" = 'EXTERNAL_PO_CREATED' AND NEW."Status" IN ('SUPPLIER_CONFIRMED','CANCELLED')) OR
        (OLD."Status" = 'SUPPLIER_CONFIRMED' AND NEW."Status" IN ('DISPATCHED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
        (OLD."Status" = 'DISPATCHED' AND NEW."Status" IN ('DELIVERED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
        (OLD."Status" = 'DELIVERED' AND NEW."Status" IN ('PARTIALLY_RECEIVED','RECEIVED','CANCELLED')) OR
        (OLD."Status" = 'PARTIALLY_RECEIVED' AND NEW."Status" IN ('RECEIVED','CANCELLED'))
    ) THEN
        RAISE EXCEPTION 'Invalid procurement handoff status transition.' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_protect_projected_supplier_quote_lineage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_projected_supplier_quote_lineage() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."SourceSupplierQuoteId" IS NOT NULL AND (
        NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
        OR NEW."SourceSupplierQuoteId" IS DISTINCT FROM OLD."SourceSupplierQuoteId"
        OR NEW."SourceSupplierQuoteRevisionId" IS DISTINCT FROM OLD."SourceSupplierQuoteRevisionId"
        OR NEW."SourceSupplierQuoteLineId" IS DISTINCT FROM OLD."SourceSupplierQuoteLineId"
        OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
        OR NEW."SourcingCaseId" IS DISTINCT FROM OLD."SourcingCaseId") THEN
        RAISE EXCEPTION USING ERRCODE = '55000',
            MESSAGE = 'Projected Supplier Quote commercial lineage is immutable';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_protect_source_document_identity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_source_document_identity() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.business_unit_id IS DISTINCT FROM OLD.business_unit_id
       OR NEW.corpus_id IS DISTINCT FROM OLD.corpus_id
       OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
       OR NEW.original_file_name IS DISTINCT FROM OLD.original_file_name
       OR NEW.byte_size IS DISTINCT FROM OLD.byte_size
       OR NEW.created_on IS DISTINCT FROM OLD.created_on THEN
        RAISE EXCEPTION 'Source document provenance is immutable' USING ERRCODE = '23514';
    END IF;

    IF OLD.security_status = 'Cleared'
       AND (NEW.object_bucket IS DISTINCT FROM OLD.object_bucket
            OR NEW.object_key IS DISTINCT FROM OLD.object_key
            OR NEW.object_version IS DISTINCT FROM OLD.object_version) THEN
        RAISE EXCEPTION 'Cleared source object identity is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_protect_source_occurrence_metadata(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_source_occurrence_metadata() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.source_metadata IS DISTINCT FROM OLD.source_metadata THEN
        RAISE EXCEPTION 'Source occurrence metadata is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_protect_supplier_quote_lineage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_supplier_quote_lineage() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
       OR NEW."SupplierId" IS DISTINCT FROM OLD."SupplierId"
       OR NEW."SupplierSolicitationId" IS DISTINCT FROM OLD."SupplierSolicitationId"
       OR NEW."SourcingCaseId" IS DISTINCT FROM OLD."SourcingCaseId"
       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial"
       OR NEW."SupplierQuoteReference" IS DISTINCT FROM OLD."SupplierQuoteReference" THEN
        RAISE EXCEPTION USING ERRCODE = '55000',
            MESSAGE = 'Supplier Quote tenant and commercial lineage are immutable';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_protect_supplier_rfq_lineage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_protect_supplier_rfq_lineage() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF (OLD."SourcingCaseId" IS NOT NULL AND NEW."SourcingCaseId" IS DISTINCT FROM OLD."SourcingCaseId")
       OR (OLD."CommercialDemandLineId" IS NOT NULL AND NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId")
       OR (OLD."NexoraSerial" IS NOT NULL AND NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial")
       OR (OLD."SupplierRfqNumber" IS NOT NULL AND NEW."SupplierRfqNumber" IS DISTINCT FROM OLD."SupplierRfqNumber") THEN
        RAISE EXCEPTION USING ERRCODE = '55000',
            MESSAGE = 'Supplier RFQ commercial lineage is write-once';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_receivable_issued_immutable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE legal_sequence bigint;
DECLARE fiscal_year integer;
DECLARE number_prefix text;
DECLARE line_count integer;
DECLARE line_subtotal numeric(18,2);
DECLARE line_discount numeric(18,2);
DECLARE line_tax numeric(18,2);
DECLARE line_total numeric(18,2);
DECLARE parent_document record;
DECLARE live_outstanding numeric(18,2);
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."DocumentNumber" IS NOT NULL
           OR NEW."IssuedOn" IS NOT NULL OR NEW."IssuedBy" IS NOT NULL
           OR NEW."VoidedOn" IS NOT NULL OR NEW."VoidReason" IS NOT NULL
           OR NEW."VoidedBy" IS NOT NULL OR NEW."Version" <> 1 THEN
            RAISE EXCEPTION 'receivable documents must be created as version-one drafts'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'receivable documents cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" IN ('Issued', 'Void', 'Cancelled') THEN
        RAISE EXCEPTION 'finalized receivable documents are immutable' USING ERRCODE = '55000';
    END IF;

    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Issued' THEN
        IF NEW."IssuedOn" IS NULL OR NEW."IssuedBy" IS NULL OR length(trim(NEW."IssuedBy")) = 0
           OR NEW."Version" <> OLD."Version" + 1
           OR NEW."VoidedOn" IS NOT NULL OR NEW."VoidReason" IS NOT NULL OR NEW."VoidedBy" IS NOT NULL
           OR (NEW."BusinessUnitId", NEW."CommercialCaseId", NEW."CustomerId", NEW."OrderId",
               NEW."ParentDocumentId", NEW."AdjustmentReasonCode", NEW."AdjustmentReason",
               NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate", NEW."DueDate",
               NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
               OLD."ParentDocumentId", OLD."AdjustmentReasonCode", OLD."AdjustmentReason",
               OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate", OLD."DueDate",
               OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
            RAISE EXCEPTION 'invalid governed receivable issue transition' USING ERRCODE = '55000';
        END IF;

        SELECT count(*)::integer,
               round(coalesce(sum(round(line."Quantity" * line."UnitPrice", 2)), 0), 2),
               round(coalesce(sum(line."DiscountAmount"), 0), 2),
               round(coalesce(sum(line."TaxAmount"), 0), 2),
               round(coalesce(sum(line."LineTotal"), 0), 2)
        INTO line_count, line_subtotal, line_discount, line_tax, line_total
        FROM public."ReceivableDocumentLines" line
        WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
          AND line."ReceivableDocumentId" = NEW."Id";
        IF line_count = 0 OR NEW."TotalAmount" <= 0
           OR line_subtotal <> NEW."SubTotal" OR line_discount <> NEW."DiscountAmount"
           OR line_tax <> NEW."TaxAmount" OR line_total <> NEW."TotalAmount"
           OR EXISTS (
               SELECT 1 FROM public."ReceivableDocumentLines" line
               WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                 AND line."ReceivableDocumentId" = NEW."Id"
                 AND line."LineTotal" <> round(round(line."Quantity" * line."UnitPrice", 2)
                     - line."DiscountAmount" + line."TaxAmount", 2)) THEN
            RAISE EXCEPTION 'receivable lines and header do not reconcile' USING ERRCODE = '23514';
        END IF;

        IF NEW."DocumentType" = 'Invoice' THEN
            IF NEW."OrderId" IS NULL THEN
                RAISE EXCEPTION 'an invoice must reference its source order' USING ERRCODE = '23514';
            END IF;
            PERFORM 1 FROM public."Orders" sales_order
            WHERE sales_order."ID" = NEW."OrderId"
              AND sales_order."BusinessUnitID" = NEW."BusinessUnitId" FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'the tenant source order does not exist' USING ERRCODE = '23503';
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM public."Orders" sales_order
                JOIN public."Setup_Master" order_status
                  ON order_status."SetupID" = sales_order."StatusID"
                 AND order_status."BusinessUnitID" = sales_order."BusinessUnitID"
                LEFT JOIN public."Quotes" quote ON quote."ID" = sales_order."QuoteID"
                 AND quote."BusinessUnitID" = sales_order."BusinessUnitID"
                LEFT JOIN public."Setup_Master" quote_status
                  ON quote_status."SetupID" = quote."StatusID"
                 AND quote_status."BusinessUnitID" = quote."BusinessUnitID"
                WHERE sales_order."ID" = NEW."OrderId"
                  AND sales_order."BusinessUnitID" = NEW."BusinessUnitId"
                  AND sales_order."IsActive"
                  AND (upper(coalesce(order_status."SetupCode", order_status."SetupValue", ''))
                         IN ('CONFIRMED', 'COMPLETED', 'SHIPPED', 'DELIVERED')
                       OR upper(coalesce(quote_status."SetupCode", quote_status."SetupValue", ''))
                         IN ('ACCEPTED', 'ORDERED'))) THEN
                RAISE EXCEPTION 'the source order is not eligible for invoicing' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                SELECT 1 FROM public."ReceivableDocumentLines" line
                LEFT JOIN public."OrderItems" order_line ON order_line."ID" = line."OrderItemId"
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                  AND line."ReceivableDocumentId" = NEW."Id"
                  AND (line."ParentDocumentLineId" IS NOT NULL OR line."OrderItemId" IS NULL
                       OR order_line."ID" IS NULL OR order_line."OrderID" <> NEW."OrderId"
                       OR line."Quantity" + coalesce((
                           SELECT sum(prior_line."Quantity")
                           FROM public."ReceivableDocumentLines" prior_line
                           JOIN public."ReceivableDocuments" prior_document
                             ON prior_document."Id" = prior_line."ReceivableDocumentId"
                            AND prior_document."BusinessUnitId" = prior_line."BusinessUnitId"
                           WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                             AND prior_line."OrderItemId" = line."OrderItemId"
                             AND prior_document."Id" <> NEW."Id"
                             AND prior_document."DocumentType" = 'Invoice'
                             AND prior_document."Status" = 'Issued'), 0) > order_line."Quantity")) THEN
                RAISE EXCEPTION 'issuing would exceed or detach a source order line' USING ERRCODE = '23514';
            END IF;
            number_prefix := 'INV';
        ELSIF NEW."DocumentType" IN ('CreditNote', 'DebitNote') THEN
            IF lower(trim(NEW."CreatedBy")) = lower(trim(NEW."IssuedBy")) THEN
                RAISE EXCEPTION 'adjustment maker and checker must be different' USING ERRCODE = '23514';
            END IF;
            SELECT parent.* INTO parent_document
            FROM public."ReceivableDocuments" parent
            WHERE parent."BusinessUnitId" = NEW."BusinessUnitId"
              AND parent."Id" = NEW."ParentDocumentId" FOR UPDATE;
            IF NOT FOUND OR parent_document."DocumentType" <> 'Invoice'
               OR parent_document."Status" <> 'Issued' THEN
                RAISE EXCEPTION 'adjustment parent must be a same-tenant issued invoice' USING ERRCODE = '23514';
            END IF;
            IF (NEW."CustomerId", NEW."CurrencyId", NEW."CommercialCaseId", NEW."OrderId")
               IS DISTINCT FROM
               (parent_document."CustomerId", parent_document."CurrencyId",
                parent_document."CommercialCaseId", parent_document."OrderId") THEN
                RAISE EXCEPTION 'adjustment ownership must match its parent invoice' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                SELECT 1
                FROM public."ReceivableDocumentLines" line
                LEFT JOIN public."ReceivableDocumentLines" parent_line
                  ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                 AND parent_line."Id" = line."ParentDocumentLineId"
                 AND parent_line."ReceivableDocumentId" = NEW."ParentDocumentId"
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                  AND line."ReceivableDocumentId" = NEW."Id"
                  AND (line."ParentDocumentLineId" IS NULL OR parent_line."Id" IS NULL
                       OR line."OrderItemId" IS DISTINCT FROM parent_line."OrderItemId"
                       OR line."Quantity" > parent_line."Quantity"
                       OR line."UnitPrice" <> parent_line."UnitPrice"
                       OR line."DiscountAmount" <> round(parent_line."DiscountAmount"
                           * line."Quantity" / parent_line."Quantity", 2)
                       OR line."TaxAmount" <> round(parent_line."TaxAmount"
                           * line."Quantity" / parent_line."Quantity", 2))) THEN
                RAISE EXCEPTION 'adjustment lines must preserve parent-line ownership and economics' USING ERRCODE = '23514';
            END IF;
            IF NEW."DocumentType" = 'CreditNote' THEN
                IF EXISTS (
                    SELECT 1
                    FROM public."ReceivableDocumentLines" line
                    JOIN public."ReceivableDocumentLines" parent_line
                      ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                     AND parent_line."Id" = line."ParentDocumentLineId"
                    WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."ReceivableDocumentId" = NEW."Id"
                      AND line."Quantity" + coalesce((
                          SELECT sum(prior_line."Quantity")
                          FROM public."ReceivableDocumentLines" prior_line
                          JOIN public."ReceivableDocuments" prior_credit
                            ON prior_credit."BusinessUnitId" = prior_line."BusinessUnitId"
                           AND prior_credit."Id" = prior_line."ReceivableDocumentId"
                          WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                            AND prior_line."ParentDocumentLineId" = line."ParentDocumentLineId"
                            AND prior_credit."ParentDocumentId" = NEW."ParentDocumentId"
                            AND prior_credit."DocumentType" = 'CreditNote'
                            AND prior_credit."Status" = 'Issued'
                            AND prior_credit."Id" <> NEW."Id"), 0) > parent_line."Quantity") THEN
                    RAISE EXCEPTION 'issued credit quantity exceeds the parent invoice line' USING ERRCODE = '23514';
                END IF;
                SELECT round(parent_document."TotalAmount"
                    - coalesce((SELECT sum(credit."TotalAmount")
                        FROM public."ReceivableDocuments" credit
                        WHERE credit."BusinessUnitId" = NEW."BusinessUnitId"
                          AND credit."ParentDocumentId" = NEW."ParentDocumentId"
                          AND credit."DocumentType" = 'CreditNote'
                          AND credit."Status" = 'Issued'
                          AND credit."Id" <> NEW."Id"), 0)
                    - coalesce((SELECT sum(allocation."Amount")
                        FROM public."PaymentAllocations" allocation
                        JOIN public."CustomerPayments" payment
                          ON payment."BusinessUnitId" = allocation."BusinessUnitId"
                         AND payment."Id" = allocation."CustomerPaymentId"
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."ReceivableDocumentId" = NEW."ParentDocumentId"
                          AND payment."Status" = 'Posted'), 0), 2)
                INTO live_outstanding;
                IF NEW."TotalAmount" > live_outstanding THEN
                    RAISE EXCEPTION 'credit note exceeds the parent invoice live outstanding balance' USING ERRCODE = '23514';
                END IF;
                number_prefix := 'CRN';
            ELSE
                IF EXISTS (
                    SELECT 1
                    FROM public."ReceivableDocumentLines" line
                    JOIN public."ReceivableDocumentLines" parent_line
                      ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                     AND parent_line."Id" = line."ParentDocumentLineId"
                    WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."ReceivableDocumentId" = NEW."Id"
                      AND line."Quantity" + coalesce((
                          SELECT sum(prior_line."Quantity")
                          FROM public."ReceivableDocumentLines" prior_line
                          JOIN public."ReceivableDocuments" prior_debit
                            ON prior_debit."BusinessUnitId" = prior_line."BusinessUnitId"
                           AND prior_debit."Id" = prior_line."ReceivableDocumentId"
                          WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                            AND prior_line."ParentDocumentLineId" = line."ParentDocumentLineId"
                            AND prior_debit."ParentDocumentId" = NEW."ParentDocumentId"
                            AND prior_debit."DocumentType" = 'DebitNote'
                            AND prior_debit."Status" = 'Issued'
                            AND prior_debit."Id" <> NEW."Id"), 0) > parent_line."Quantity") THEN
                    RAISE EXCEPTION 'issued debit quantity exceeds the parent invoice line' USING ERRCODE = '23514';
                END IF;
                number_prefix := 'DBN';
            END IF;
        ELSE
            RAISE EXCEPTION 'unsupported receivable document type' USING ERRCODE = '23514';
        END IF;

        fiscal_year := extract(year from NEW."DocumentDate")::integer;
        INSERT INTO public."LegalDocumentCounters"
            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
        VALUES (NEW."BusinessUnitId", NEW."DocumentType", fiscal_year, 2)
        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
        RETURNING "NextNumber" - 1 INTO legal_sequence;
        NEW."DocumentNumber" := format('%s-%s-%s', number_prefix, fiscal_year,
            lpad(legal_sequence::text, 6, '0'));
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", 'Issued', NEW."IssuedBy", jsonb_build_object(
                'number', NEW."DocumentNumber", 'documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'reasonCode', NEW."AdjustmentReasonCode",
                'amount', NEW."TotalAmount", 'version', NEW."Version"), NEW."IssuedOn");
        RETURN NEW;
    END IF;

    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
        IF NEW."DocumentNumber" IS NOT NULL OR NEW."IssuedOn" IS NOT NULL
           OR NEW."VoidedOn" IS NULL OR NEW."VoidReason" IS NULL OR length(trim(NEW."VoidReason")) = 0
           OR NEW."VoidedBy" IS NULL OR length(trim(NEW."VoidedBy")) = 0
           OR NEW."Version" <> OLD."Version" + 1
           OR (NEW."BusinessUnitId", NEW."CommercialCaseId", NEW."CustomerId", NEW."OrderId",
               NEW."ParentDocumentId", NEW."AdjustmentReasonCode", NEW."AdjustmentReason",
               NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate", NEW."DueDate",
               NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn", NEW."IssuedBy")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
               OLD."ParentDocumentId", OLD."AdjustmentReasonCode", OLD."AdjustmentReason",
               OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate", OLD."DueDate",
               OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn", OLD."IssuedBy") THEN
            RAISE EXCEPTION 'invalid governed receivable cancellation transition' USING ERRCODE = '55000';
        END IF;
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", 'DraftCancelled', NEW."VoidedBy",
            jsonb_build_object('reason', NEW."VoidReason", 'documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'amount', NEW."TotalAmount",
                'version', NEW."Version"), NEW."VoidedOn");
        RETURN NEW;
    END IF;
    IF NEW."Status" IS DISTINCT FROM OLD."Status" THEN
        RAISE EXCEPTION 'invalid receivable document status transition' USING ERRCODE = '55000';
    END IF;
    RAISE EXCEPTION 'receivable drafts are immutable; cancel and recreate the draft' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_receivable_line_issued_immutable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_receivable_line_issued_immutable() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE target_document_id bigint;
DECLARE target_business_unit_id bigint;
DECLARE target_status text;
BEGIN
    target_document_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."ReceivableDocumentId" ELSE NEW."ReceivableDocumentId" END;
    target_business_unit_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."BusinessUnitId" ELSE NEW."BusinessUnitId" END;
    SELECT document."Status" INTO target_status
    FROM public."ReceivableDocuments" document
    WHERE document."Id" = target_document_id
      AND document."BusinessUnitId" = target_business_unit_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'receivable line tenant document does not exist' USING ERRCODE = '23503';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF target_status <> 'Draft' OR EXISTS (
            SELECT 1 FROM public."FinanceOutboxMessages" evidence
            WHERE evidence."BusinessUnitId" = target_business_unit_id
              AND evidence."AggregateType" = 'ReceivableDocument'
              AND evidence."AggregateId" = target_document_id) THEN
            RAISE EXCEPTION 'receivable lines may only be inserted with their new draft document' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'receivable document lines are immutable; cancel and recreate the draft' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_receivable_live_outstanding(bigint, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_receivable_live_outstanding(business_unit_id bigint, document_id bigint) RETURNS numeric
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE document_row record;
DECLARE issued_credits numeric(18,2);
DECLARE posted_payments numeric(18,2);
DECLARE posted_write_offs numeric(18,2);
BEGIN
    SELECT document.* INTO document_row
    FROM public."ReceivableDocuments" document
    WHERE document."BusinessUnitId" = business_unit_id AND document."Id" = document_id
    FOR UPDATE;
    IF NOT FOUND OR document_row."Status" <> 'Issued'
       OR document_row."DocumentType" NOT IN ('Invoice', 'DebitNote') THEN
        RAISE EXCEPTION 'write-off requires a same-tenant issued invoice or debit note' USING ERRCODE = '23514';
    END IF;
    issued_credits := 0;
    IF document_row."DocumentType" = 'Invoice' THEN
        SELECT coalesce(sum(credit."TotalAmount"), 0) INTO issued_credits
        FROM public."ReceivableDocuments" credit
        WHERE credit."BusinessUnitId" = business_unit_id
          AND credit."ParentDocumentId" = document_id
          AND credit."DocumentType" = 'CreditNote' AND credit."Status" = 'Issued';
    END IF;
    SELECT coalesce(sum(allocation."Amount"), 0) INTO posted_payments
    FROM public."PaymentAllocations" allocation
    JOIN public."CustomerPayments" payment
      ON payment."BusinessUnitId" = allocation."BusinessUnitId"
     AND payment."Id" = allocation."CustomerPaymentId"
    WHERE allocation."BusinessUnitId" = business_unit_id
      AND allocation."ReceivableDocumentId" = document_id
      AND payment."Status" = 'Posted';
    SELECT coalesce(sum(allocation."Amount"), 0) INTO posted_write_offs
    FROM public."WriteOffAllocations" allocation
    JOIN public."ReceivableWriteOffs" write_off
      ON write_off."BusinessUnitId" = allocation."BusinessUnitId"
     AND write_off."Id" = allocation."ReceivableWriteOffId"
    WHERE allocation."BusinessUnitId" = business_unit_id
      AND allocation."ReceivableDocumentId" = document_id
      AND write_off."Status" = 'Posted';
    RETURN round(document_row."TotalAmount" - issued_credits - posted_payments - posted_write_offs, 2);
END
$$;


--
-- Name: nexora_receivable_order_item_valid(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_receivable_order_item_valid() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."OrderItemId" IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM public."ReceivableDocuments" document
        JOIN public."OrderItems" item ON item."ID" = NEW."OrderItemId"
        JOIN public."Orders" sales_order ON sales_order."ID" = item."OrderID"
        WHERE document."Id" = NEW."ReceivableDocumentId"
          AND document."BusinessUnitId" = NEW."BusinessUnitId"
          AND sales_order."ID" = document."OrderId"
          AND sales_order."BusinessUnitID" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'receivable line order item does not belong to the tenant order' USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_receivable_outbox_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_receivable_outbox_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE event_type text;
DECLARE event_time timestamp without time zone;
DECLARE event_payload jsonb;
DECLARE type_segment text;
BEGIN
    type_segment := CASE NEW."DocumentType"
        WHEN 'CreditNote' THEN 'credit-note'
        WHEN 'DebitNote' THEN 'debit-note'
        ELSE 'receivable' END;
    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
        event_type := 'finance.' || type_segment || '.draft-created';
        event_time := coalesce(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", CASE NEW."DocumentType" WHEN 'Invoice' THEN 'DraftCreated'
                ELSE 'AdjustmentDraftCreated' END, NEW."CreatedBy",
            jsonb_build_object('documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'amount', NEW."TotalAmount",
                'version', NEW."Version"), event_time);
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" IN ('Issued', 'Cancelled') THEN
        event_type := 'finance.' || type_segment ||
            CASE NEW."Status" WHEN 'Issued' THEN '.issued' ELSE '.cancelled' END;
        event_time := CASE NEW."Status" WHEN 'Issued' THEN NEW."IssuedOn" ELSE NEW."VoidedOn" END;
    ELSE
        RETURN NEW;
    END IF;
    event_payload := jsonb_build_object(
        'Id', NEW."Id", 'OrderId', NEW."OrderId", 'ParentDocumentId', NEW."ParentDocumentId",
        'DocumentType', NEW."DocumentType", 'Status', NEW."Status",
        'DocumentNumber', NEW."DocumentNumber", 'TotalAmount', NEW."TotalAmount",
        'CurrencyId', NEW."CurrencyId", 'CustomerId', NEW."CustomerId",
        'CommercialCaseId', NEW."CommercialCaseId", 'ReasonCode', NEW."AdjustmentReasonCode",
        'Actor', coalesce(NEW."IssuedBy", NEW."VoidedBy", NEW."CreatedBy"),
        'CreatedBy', NEW."CreatedBy", 'IssuedBy', NEW."IssuedBy", 'Version', NEW."Version");
    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'ReceivableDocument',
        NEW."Id", NEW."Version", event_type, event_payload, event_time);
    RETURN NEW;
END
$$;


--
-- Name: nexora_record_lead_status_history(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_record_lead_status_history() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE actor text; actor_source text; transition_reason text;
BEGIN
    IF TG_OP = 'INSERT' THEN
        actor := NULLIF(NEW."CreatedBy", ''); actor_source := 'LeadCreation'; transition_reason := NULL;
    ELSE
        actor := NULLIF(current_setting('nexora.actor', true), '');
        actor_source := COALESCE(NULLIF(current_setting('nexora.actor_source', true), ''), 'DatabaseTrigger');
        transition_reason := COALESCE(NULLIF(current_setting('nexora.reason', true), ''), NEW."AssignComment");
    END IF;
    IF TG_OP = 'INSERT' OR NEW."LeadStatusId" IS DISTINCT FROM OLD."LeadStatusId" THEN
        INSERT INTO "LeadStatusHistories"
            ("LeadID", "CommercialCaseID", "BusinessUnitID", "PreviousStatusID", "NewStatusID",
             "EventType", "ChangedBy", "ActorSource", "ChangedOn", "Reason", "CommercialCaseReference")
        VALUES (NEW."ID", NEW."CommercialCaseId", NEW."BusinessUnitID",
            CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD."LeadStatusId" END,
            NEW."LeadStatusId", CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE 'StatusChanged' END,
            actor, actor_source, now(), transition_reason, NEW."CommercialCaseReference");
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_refund_governed(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_refund_governed() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE payment_row record;
DECLARE allocated_amount numeric(18,2);
DECLARE reserved_amount numeric(18,2);
DECLARE legal_sequence bigint;
DECLARE fiscal_year integer;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'customer refunds cannot be deleted' USING ERRCODE = '55000';
    END IF;
    SELECT payment.* INTO payment_row
    FROM public."CustomerPayments" payment
    WHERE payment."BusinessUnitId" = NEW."BusinessUnitId"
      AND payment."Id" = NEW."SourcePaymentId" FOR UPDATE;
    IF NOT FOUND OR payment_row."Status" <> 'Posted'
       OR (NEW."CustomerId", NEW."CurrencyId", NEW."CommercialCaseId") IS DISTINCT FROM
          (payment_row."CustomerId", payment_row."CurrencyId", payment_row."CommercialCaseId") THEN
        RAISE EXCEPTION 'refund source receipt identity or status is invalid' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."RefundNumber" IS NOT NULL OR NEW."Version" <> 1
           OR NOT NEW."DestinationVerified" OR length(trim(NEW."DestinationReference")) = 0
           OR length(trim(NEW."Method")) = 0 OR length(trim(NEW."ReasonCode")) = 0
           OR length(trim(NEW."Reason")) = 0 OR length(trim(NEW."CreatedBy")) = 0
           OR NEW."PostingStatus" <> 'NotReleased' OR NEW."JournalReference" IS NOT NULL
           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
           OR NEW."ReleasedBy" IS NOT NULL OR NEW."ReleasedOn" IS NOT NULL
           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
           OR NEW."DisbursementFailureReason" IS NOT NULL
           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL THEN
            RAISE EXCEPTION 'refunds must be created as clean version-one drafts' USING ERRCODE = '23514';
        END IF;
        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
        FROM public."PaymentAllocations" allocation
        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
          AND allocation."CustomerPaymentId" = NEW."SourcePaymentId";
        SELECT coalesce(sum(refund."Amount"), 0) INTO reserved_amount
        FROM public."CustomerRefunds" refund
        WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
          AND refund."SourcePaymentId" = NEW."SourcePaymentId"
          AND refund."Status" IN ('Approved', 'Released');
        IF NEW."Amount" > round(payment_row."Amount" - allocated_amount - reserved_amount, 2) THEN
            RAISE EXCEPTION 'refund exceeds the live unapplied receipt balance' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF (NEW."BusinessUnitId", NEW."SourcePaymentId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
        NEW."RequestedExecutionDate", NEW."Amount", NEW."Method", NEW."DestinationReference", NEW."DestinationVerified",
        NEW."ReasonCode", NEW."Reason", NEW."EvidenceReference", NEW."IdempotencyKey", NEW."RequestHash",
        NEW."CreatedBy", NEW."CreatedOn") IS DISTINCT FROM
       (OLD."BusinessUnitId", OLD."SourcePaymentId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
        OLD."RequestedExecutionDate", OLD."Amount", OLD."Method", OLD."DestinationReference", OLD."DestinationVerified",
        OLD."ReasonCode", OLD."Reason", OLD."EvidenceReference", OLD."IdempotencyKey", OLD."RequestHash",
        OLD."CreatedBy", OLD."CreatedOn") OR NEW."Version" <> OLD."Version" + 1 THEN
        RAISE EXCEPTION 'refund identity and request fields are immutable' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
        IF NEW."ApprovedBy" IS NULL OR length(trim(NEW."ApprovedBy")) = 0
           OR lower(trim(NEW."ApprovedBy")) = lower(trim(OLD."CreatedBy")) OR NEW."ApprovedOn" IS NULL
           OR NEW."PostingStatus" <> 'Reserved' OR NEW."RefundNumber" IS NOT NULL
           OR NEW."ReleasedBy" IS NOT NULL OR NEW."ReleasedOn" IS NOT NULL
           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
           OR NEW."DisbursementFailureReason" IS NOT NULL
           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
            RAISE EXCEPTION 'invalid governed refund approval transition' USING ERRCODE = '55000';
        END IF;
        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
        FROM public."PaymentAllocations" allocation
        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
          AND allocation."CustomerPaymentId" = NEW."SourcePaymentId";
        SELECT coalesce(sum(refund."Amount"), 0) INTO reserved_amount
        FROM public."CustomerRefunds" refund
        WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
          AND refund."SourcePaymentId" = NEW."SourcePaymentId"
          AND refund."Id" <> NEW."Id" AND refund."Status" IN ('Approved', 'Released');
        IF NEW."Amount" > round(payment_row."Amount" - allocated_amount - reserved_amount, 2) THEN
            RAISE EXCEPTION 'refund approval exceeds the live unapplied receipt balance' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Approved' AND NEW."Status" = 'Released' THEN
        IF NEW."ReleasedBy" IS NULL OR length(trim(NEW."ReleasedBy")) = 0
           OR lower(trim(NEW."ReleasedBy")) IN (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")))
           OR NEW."ReleasedOn" IS NULL OR NEW."RefundNumber" IS NULL
           OR NEW."PostingStatus" <> 'PendingDisbursement'
           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
           OR NEW."DisbursementFailureReason" IS NOT NULL
           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
            RAISE EXCEPTION 'invalid governed refund release transition' USING ERRCODE = '55000';
        END IF;
        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
        FROM public."PaymentAllocations" allocation
        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
          AND allocation."CustomerPaymentId" = NEW."SourcePaymentId";
        SELECT coalesce(sum(refund."Amount"), 0) INTO reserved_amount
        FROM public."CustomerRefunds" refund
        WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
          AND refund."SourcePaymentId" = NEW."SourcePaymentId"
          AND refund."Id" <> NEW."Id" AND refund."Status" IN ('Approved', 'Released');
        IF NEW."Amount" > round(payment_row."Amount" - allocated_amount - reserved_amount, 2) THEN
            RAISE EXCEPTION 'refund release exceeds the live unapplied receipt balance' USING ERRCODE = '23514';
        END IF;
        fiscal_year := extract(year from NEW."RequestedExecutionDate")::integer;
        INSERT INTO public."LegalDocumentCounters"
            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
        VALUES (NEW."BusinessUnitId", 'Refund', fiscal_year, 2)
        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
        RETURNING "NextNumber" - 1 INTO legal_sequence;
        NEW."RefundNumber" := format('RFD-%s-%s', fiscal_year, lpad(legal_sequence::text, 6, '0'));
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Released' AND NEW."Status" = 'Released'
       AND OLD."PostingStatus" = 'PendingDisbursement'
       AND NEW."PostingStatus" IN ('Settled', 'Failed') THEN
        IF NEW."DisbursementUpdatedBy" IS NULL OR length(trim(NEW."DisbursementUpdatedBy")) = 0
           OR lower(trim(NEW."DisbursementUpdatedBy")) IN
              (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")), lower(trim(OLD."ReleasedBy")))
           OR NEW."DisbursementUpdatedOn" IS NULL OR NEW."JournalReference" IS NULL
           OR length(trim(NEW."JournalReference")) = 0
           OR NEW."RefundNumber" IS DISTINCT FROM OLD."RefundNumber"
           OR (NEW."PostingStatus" = 'Settled' AND NEW."DisbursementFailureReason" IS NOT NULL)
           OR (NEW."PostingStatus" = 'Failed' AND
               (NEW."DisbursementFailureReason" IS NULL OR length(trim(NEW."DisbursementFailureReason")) = 0))
           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
           OR NEW."ReleasedBy" IS DISTINCT FROM OLD."ReleasedBy" OR NEW."ReleasedOn" IS DISTINCT FROM OLD."ReleasedOn"
           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"
           OR NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
           OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
           OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference" THEN
            RAISE EXCEPTION 'invalid governed refund disbursement transition' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" IN ('Draft', 'Approved') AND NEW."Status" = 'Cancelled' THEN
        IF NEW."CancelledBy" IS NULL OR length(trim(NEW."CancelledBy")) = 0
           OR NEW."CancelledOn" IS NULL OR NEW."CancellationReason" IS NULL
           OR length(trim(NEW."CancellationReason")) = 0 OR NEW."PostingStatus" <> 'Cancelled'
           OR NEW."RefundNumber" IS NOT NULL OR NEW."ReleasedBy" IS NOT NULL OR NEW."ReleasedOn" IS NOT NULL
           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
           OR NEW."DisbursementFailureReason" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
           OR (OLD."Status" = 'Approved' AND lower(trim(NEW."CancelledBy")) = lower(trim(OLD."CreatedBy")))
           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
            RAISE EXCEPTION 'invalid governed refund cancellation transition' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Released' AND NEW."Status" = 'Reversed' THEN
        IF NEW."ReversedBy" IS NULL OR length(trim(NEW."ReversedBy")) = 0
           OR lower(trim(NEW."ReversedBy")) IN
              (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")), lower(trim(OLD."ReleasedBy")))
           OR NEW."ReversedOn" IS NULL OR NEW."ReversalReason" IS NULL
           OR length(trim(NEW."ReversalReason")) = 0
           OR NEW."ReversalEvidenceReference" IS NULL OR length(trim(NEW."ReversalEvidenceReference")) = 0
           OR OLD."PostingStatus" <> 'Failed' OR NEW."PostingStatus" <> 'ReversalPendingExport'
           OR NEW."RefundNumber" IS DISTINCT FROM OLD."RefundNumber"
           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
           OR NEW."ReleasedBy" IS DISTINCT FROM OLD."ReleasedBy" OR NEW."ReleasedOn" IS DISTINCT FROM OLD."ReleasedOn"
           OR NEW."DisbursementUpdatedBy" IS DISTINCT FROM OLD."DisbursementUpdatedBy"
           OR NEW."DisbursementUpdatedOn" IS DISTINCT FROM OLD."DisbursementUpdatedOn"
           OR NEW."DisbursementFailureReason" IS DISTINCT FROM OLD."DisbursementFailureReason"
           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"
           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
            RAISE EXCEPTION 'invalid governed refund reversal transition' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'refund fields and lifecycle are immutable outside governed transitions' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_refund_outbox_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_refund_outbox_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE event_action text;
DECLARE event_time timestamp without time zone;
DECLARE event_actor text;
BEGIN
    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
        event_action := 'DraftCreated'; event_time := NEW."CreatedOn"; event_actor := NEW."CreatedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
        event_action := 'Approved'; event_time := NEW."ApprovedOn"; event_actor := NEW."ApprovedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Approved' AND NEW."Status" = 'Released' THEN
        event_action := 'Released'; event_time := NEW."ReleasedOn"; event_actor := NEW."ReleasedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Released' AND NEW."Status" = 'Released'
          AND OLD."PostingStatus" = 'PendingDisbursement' AND NEW."PostingStatus" = 'Settled' THEN
        event_action := 'DisbursementConfirmed'; event_time := NEW."DisbursementUpdatedOn"; event_actor := NEW."DisbursementUpdatedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Released' AND NEW."Status" = 'Released'
          AND OLD."PostingStatus" = 'PendingDisbursement' AND NEW."PostingStatus" = 'Failed' THEN
        event_action := 'DisbursementFailed'; event_time := NEW."DisbursementUpdatedOn"; event_actor := NEW."DisbursementUpdatedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" IN ('Draft', 'Approved') AND NEW."Status" = 'Cancelled' THEN
        event_action := 'Cancelled'; event_time := NEW."CancelledOn"; event_actor := NEW."CancelledBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Released' AND NEW."Status" = 'Reversed' THEN
        event_action := 'Reversed'; event_time := NEW."ReversedOn"; event_actor := NEW."ReversedBy";
    ELSE RETURN NEW;
    END IF;
    PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'CustomerRefund',
        NEW."Id", event_action, event_actor, jsonb_build_object(
            'number', NEW."RefundNumber", 'sourcePaymentId', NEW."SourcePaymentId",
            'amount', NEW."Amount", 'reasonCode', NEW."ReasonCode", 'version', NEW."Version"), event_time);
    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerRefund',
        NEW."Id", NEW."Version", 'finance.refund.' || CASE event_action
            WHEN 'DraftCreated' THEN 'draft-created'
            WHEN 'DisbursementConfirmed' THEN 'disbursement-confirmed'
            WHEN 'DisbursementFailed' THEN 'disbursement-failed'
            ELSE lower(event_action) END,
        jsonb_build_object('Id', NEW."Id", 'Status', NEW."Status",
            'RefundNumber', NEW."RefundNumber", 'SourcePaymentId', NEW."SourcePaymentId",
            'CustomerId', NEW."CustomerId", 'CommercialCaseId', NEW."CommercialCaseId",
            'CurrencyId', NEW."CurrencyId", 'Amount', NEW."Amount",
            'DestinationToken', CASE WHEN event_action = 'Released' THEN NEW."DestinationReference" ELSE NULL END,
            'ReasonCode', NEW."ReasonCode", 'PostingStatus', NEW."PostingStatus",
            'ProviderReference', NEW."JournalReference",
            'Actor', event_actor, 'Version', NEW."Version"), event_time);
    RETURN NEW;
END
$$;


--
-- Name: nexora_reject_ai_ledger_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_ai_ledger_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'AI accounting history is immutable' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_reject_classification_source_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_classification_source_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.business_unit_id IS DISTINCT FROM OLD.business_unit_id
       OR NEW.source_document_id IS DISTINCT FROM OLD.source_document_id
       OR NEW.source_document_content_hash IS DISTINCT FROM OLD.source_document_content_hash
       OR NEW.source_object_version IS DISTINCT FROM OLD.source_object_version THEN
        RAISE EXCEPTION USING ERRCODE = '55000',
            MESSAGE = 'commercial document source identity is immutable';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_reject_commercial_demand_line_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_commercial_demand_line_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING ERRCODE = '55000',
        MESSAGE = 'commercial Demand Line identity is immutable';
END
$$;


--
-- Name: nexora_reject_commercial_exception_event_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_commercial_exception_event_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'commercial exception events are append-only';
END;
$$;


--
-- Name: nexora_reject_commercial_exception_operation_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_commercial_exception_operation_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'commercial exception operation receipts are append-only';
END;
$$;


--
-- Name: nexora_reject_extraction_dead_letter_event_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_extraction_dead_letter_event_mutation() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    RAISE EXCEPTION 'extraction dead-letter events are append-only' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_reject_lead_review_audit_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_lead_review_audit_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Lead review audit records are immutable' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_reject_learning_governance_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_learning_governance_mutation() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    RAISE EXCEPTION 'learning governance events are append-only' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_reject_master_data_audit_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_master_data_audit_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = '55000',
        MESSAGE = 'master data audit rows are append-only';
END
$$;


--
-- Name: nexora_reject_opportunity_immutable_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_opportunity_immutable_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'commercial opportunity intelligence records are append-only';
END;
$$;


--
-- Name: nexora_reject_procurement_event_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_procurement_event_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = '55000',
        MESSAGE = 'procurement events are append-only';
END
$$;


--
-- Name: nexora_reject_referenced_inventory_tenant_change(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_referenced_inventory_tenant_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."Buid" IS DISTINCT FROM OLD."Buid" AND EXISTS (
        SELECT 1 FROM supplier_purchase_order_lines line
        WHERE line."InventoryId" = OLD."Id"
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '23503',
            MESSAGE = 'inventory tenant ownership is immutable while referenced by procurement';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_reject_referenced_product_tenant_change(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_referenced_product_tenant_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."BUID" IS DISTINCT FROM OLD."BUID" AND (
        EXISTS (SELECT 1 FROM "SupplierQuotedItems" quote WHERE quote."ProductId" = OLD."ID")
        OR EXISTS (SELECT 1 FROM supplier_purchase_order_lines line WHERE line."ProductId" = OLD."ID")
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '23503',
            MESSAGE = 'product tenant ownership is immutable while referenced by procurement';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_reject_routing_decision_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_routing_decision_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'routing decision history is append-only';
END $$;


--
-- Name: nexora_reject_sales_coaching_ack_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_sales_coaching_ack_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING ERRCODE = '55000',
        MESSAGE = 'sales coaching acknowledgements are append-only';
END
$$;


--
-- Name: nexora_reject_sales_event_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_sales_event_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'commercial event history is append-only';
END $$;


--
-- Name: nexora_reject_sourcing_case_lineage_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_sourcing_case_lineage_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
       OR NEW."CommercialDemandLineId" IS DISTINCT FROM OLD."CommercialDemandLineId"
       OR NEW."RfqId" IS DISTINCT FROM OLD."RfqId"
       OR NEW."RfqItemId" IS DISTINCT FROM OLD."RfqItemId"
       OR NEW."NexoraSerial" IS DISTINCT FROM OLD."NexoraSerial" THEN
        RAISE EXCEPTION USING ERRCODE = '55000',
            MESSAGE = 'Sourcing Case tenant and commercial lineage are immutable';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_reject_supplier_negotiation_decision_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_supplier_negotiation_decision_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING ERRCODE = '55000',
        MESSAGE = 'supplier negotiation decisions are append-only';
END
$$;


--
-- Name: nexora_reject_supplier_quote_append_only_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_reject_supplier_quote_append_only_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION USING ERRCODE = '55000',
        MESSAGE = TG_TABLE_NAME || ' is append-only';
END
$$;


--
-- Name: nexora_release01a_forbid_history_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_release01a_forbid_history_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  RAISE EXCEPTION 'Release 01A identity history is append-only';
END $$;


--
-- Name: nexora_release01a_occurrence_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_release01a_occurrence_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
     OR NEW."BatchId" IS DISTINCT FROM OLD."BatchId"
     OR NEW."SourceChannel" IS DISTINCT FROM OLD."SourceChannel"
     OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
     OR NEW."ExternalSourceId" IS DISTINCT FROM OLD."ExternalSourceId"
     OR NEW."EmailThreadId" IS DISTINCT FROM OLD."EmailThreadId"
     OR NEW."SourceSystem" IS DISTINCT FROM OLD."SourceSystem"
     OR NEW."Sender" IS DISTINCT FROM OLD."Sender"
     OR NEW."Subject" IS DISTINCT FROM OLD."Subject"
     OR NEW."OriginalFileName" IS DISTINCT FROM OLD."OriginalFileName"
     OR NEW."MimeType" IS DISTINCT FROM OLD."MimeType"
     OR NEW."FileSize" IS DISTINCT FROM OLD."FileSize"
     OR NEW."ContentHash" IS DISTINCT FROM OLD."ContentHash"
     OR NEW."SourceDocumentId" IS DISTINCT FROM OLD."SourceDocumentId"
     OR NEW."ExtractionJobId" IS DISTINCT FROM OLD."ExtractionJobId"
     OR NEW."CustomerScopeKey" IS DISTINCT FROM OLD."CustomerScopeKey"
     OR NEW."LogicalInquiryFingerprint" IS DISTINCT FROM OLD."LogicalInquiryFingerprint"
     OR NEW."PolicyVersion" IS DISTINCT FROM OLD."PolicyVersion"
     OR NEW."ProcessingPath" IS DISTINCT FROM OLD."ProcessingPath"
     OR NEW."ExternalAiUsed" IS DISTINCT FROM OLD."ExternalAiUsed"
     OR NEW."ExternalCost" IS DISTINCT FROM OLD."ExternalCost"
     OR NEW."SourceReceivedAtUtc" IS DISTINCT FROM OLD."SourceReceivedAtUtc"
     OR NEW."IngestedAtUtc" IS DISTINCT FROM OLD."IngestedAtUtc"
     OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
     OR NEW."ActorType" IS DISTINCT FROM OLD."ActorType"
     OR NEW."ActorId" IS DISTINCT FROM OLD."ActorId"
     OR NEW."CorrelationId" IS DISTINCT FROM OLD."CorrelationId" THEN
    RAISE EXCEPTION 'Ingestion provenance is immutable';
  END IF;
  RETURN NEW;
END $$;


--
-- Name: nexora_release01b_contact_tenant_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_release01b_contact_tenant_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."CustomerID" IS NULL AND NEW."SupplierID" IS NULL THEN
        RAISE EXCEPTION 'Contact requires a tenant-owned Customer or Supplier' USING ERRCODE = '23514';
    END IF;
    IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Customers" customer
        WHERE customer."ID" = NEW."CustomerID" AND customer."BUID" = NEW."BusinessUnitID") THEN
        RAISE EXCEPTION 'Contact Customer tenant mismatch' USING ERRCODE = '23503';
    END IF;
    IF NEW."SupplierID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Suppliers" supplier
        WHERE supplier."ID" = NEW."SupplierID" AND supplier."BUID" = NEW."BusinessUnitID") THEN
        RAISE EXCEPTION 'Contact Supplier tenant mismatch' USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_release01b_intake_before_claim_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_release01b_intake_before_claim_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."Status" = 'Leased' AND OLD."Status" IS DISTINCT FROM NEW."Status" AND NOT EXISTS (
        SELECT 1 FROM source_document_occurrences occurrence
        WHERE occurrence.business_unit_id = NEW."BusinessUnitId"
          AND occurrence.id = NEW."SourceDocumentOccurrenceId"
          AND occurrence.extraction_job_id = NEW."Id"
          AND (occurrence.intake_status IN ('Queued', 'Retryable')
            OR (occurrence.intake_status = 'Processing'
              AND OLD."Status" IN ('Leased', 'Extracting', 'Persisting')
              AND (OLD."LeaseExpiresAt" IS NULL OR OLD."LeaseExpiresAt" <= now())))) THEN
        RAISE EXCEPTION 'Extraction cannot start before its durable intake occurrence is queued' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_release01b_lead_occurrence_source_guard(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_release01b_lead_occurrence_source_guard() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."SourceDocumentOccurrenceId" IS DISTINCT FROM OLD."SourceDocumentOccurrenceId"
       OR NEW."LogicalGroupKey" IS DISTINCT FROM OLD."LogicalGroupKey"
       OR NEW."RecordKind" IS DISTINCT FROM OLD."RecordKind" THEN
        RAISE EXCEPTION 'Lead occurrence source linkage is immutable' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_release01c_sync_intake_from_job(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_release01c_sync_intake_from_job() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    next_status text;
    error_category text;
    error_code text;
    error_details jsonb;
BEGIN
    IF NEW."SourceDocumentOccurrenceId" IS NULL OR NEW."Status" IS NOT DISTINCT FROM OLD."Status" THEN
        RETURN NEW;
    END IF;

    IF NEW."Status" IN ('Leased', 'Extracting') THEN
        next_status := 'Processing';
    ELSIF NEW."Status" = 'Pending' AND OLD."Status" IN ('Leased', 'Extracting', 'Persisting') THEN
        next_status := 'Retryable';
        error_category := 'Extraction';
        error_code := 'extraction_retryable';
    ELSIF NEW."Status" = 'Pending' AND OLD."Status" = 'DeadLetter' THEN
        next_status := 'Queued';
    ELSIF NEW."Status" = 'DeadLetter' THEN
        next_status := 'DeadLetter';
        error_category := 'Extraction';
        error_code := 'extraction_dead_letter';
    ELSIF NEW."Status" = 'Succeeded' THEN
        next_status := 'Resolved';
    ELSE
        RETURN NEW;
    END IF;

    IF error_code IS NOT NULL THEN
        error_details := jsonb_build_object(
            'attempt', NEW."Attempts",
            'maxAttempts', NEW."MaxAttempts",
            'message', left(COALESCE(NEW."LastError", ''), 1000));
    END IF;

    UPDATE source_document_occurrences AS intake
    SET intake_status = CASE
            WHEN NEW."Status" = 'Succeeded' AND EXISTS (
                SELECT 1 FROM "LeadIngestionOccurrences" reconciliation
                WHERE reconciliation."BusinessUnitId" = NEW."BusinessUnitId"
                  AND reconciliation."SourceDocumentOccurrenceId" = intake.id
                  AND reconciliation."Classification" = 'PossibleMatchReviewRequired')
                THEN 'ReviewRequired'
            ELSE next_status
        END,
        processing_reused = CASE
            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                THEN true ELSE intake.processing_reused END,
        parser_reused = CASE
            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                THEN true ELSE intake.parser_reused END,
        ocr_reused = CASE
            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                THEN true ELSE intake.ocr_reused END,
        local_model_reused = CASE
            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                THEN true ELSE intake.local_model_reused END,
        external_model_reused = CASE
            WHEN NEW."Status" = 'Succeeded' AND intake.id <> NEW."SourceDocumentOccurrenceId"
                THEN true ELSE intake.external_model_reused END,
        last_error_category = error_category,
        last_error_code = error_code,
        last_error_details = error_details,
        updated_on = now()
    WHERE intake.business_unit_id = NEW."BusinessUnitId"
      AND intake.extraction_job_id = NEW."Id";
    RETURN NEW;
END; $$;


--
-- Name: nexora_require_commercial_exception_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_require_commercial_exception_event() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NOT EXISTS (
            SELECT 1
            FROM public.commercial_exception_events event
            WHERE event."BusinessUnitId" = NEW."BusinessUnitId"
              AND event."CommercialExceptionCaseId" = NEW."Id"
              AND event."FromStatus" IS NULL
              AND event."FromVersion" = 0
              AND event."ToStatus" = NEW."Status"
              AND event."ToVersion" = NEW."Version"
        ) THEN
            RAISE EXCEPTION 'commercial exception creation requires a matching append-only event';
        END IF;
    ELSIF ROW(
        NEW."SourceVersion", NEW."OwnerUserId", NEW."Severity", NEW."Status",
        NEW."ReasonCode", NEW."Title", NEW."Summary", NEW."RecommendedActionCode",
        NEW."EvidenceJson", NEW."RuleVersion", NEW."LastDetectedAtUtc",
        NEW."SlaDueAtUtc", NEW."ResolvedAtUtc", NEW."Version"
    ) IS DISTINCT FROM ROW(
        OLD."SourceVersion", OLD."OwnerUserId", OLD."Severity", OLD."Status",
        OLD."ReasonCode", OLD."Title", OLD."Summary", OLD."RecommendedActionCode",
        OLD."EvidenceJson", OLD."RuleVersion", OLD."LastDetectedAtUtc",
        OLD."SlaDueAtUtc", OLD."ResolvedAtUtc", OLD."Version"
    ) THEN
        IF NEW."Version" <> OLD."Version" + 1 OR NOT EXISTS (
            SELECT 1
            FROM public.commercial_exception_events event
            WHERE event."BusinessUnitId" = NEW."BusinessUnitId"
              AND event."CommercialExceptionCaseId" = NEW."Id"
              AND event."FromStatus" IS NOT DISTINCT FROM OLD."Status"
              AND event."FromVersion" = OLD."Version"
              AND event."ToStatus" = NEW."Status"
              AND event."ToVersion" = NEW."Version"
        ) THEN
            RAISE EXCEPTION 'commercial exception material changes require the next version and a matching append-only event';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;


--
-- Name: nexora_require_lifecycle_command(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_require_lifecycle_command() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF current_setting('nexora.lifecycle_command', true) IS DISTINCT FROM 'true'
       OR NEW."LifecycleVersion" <> OLD."LifecycleVersion" + 1 THEN
        RAISE EXCEPTION USING ERRCODE = '55000',
            MESSAGE = 'Status must be changed through the governed lifecycle command.';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_require_opportunity_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_require_opportunity_event() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE source_kind text;
DECLARE recommendation_id bigint;
BEGIN
    IF TG_TABLE_NAME = 'commercial_opportunity_recommendations' THEN
        source_kind := 'Recommendation'; recommendation_id := NEW."Id";
    ELSIF TG_TABLE_NAME = 'commercial_opportunity_feedback' THEN
        source_kind := 'Feedback'; recommendation_id := NEW."OpportunityRecommendationId";
    ELSE
        source_kind := 'Outcome'; recommendation_id := NEW."OpportunityRecommendationId";
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.commercial_opportunity_events event
        WHERE event."BusinessUnitId" = NEW."BusinessUnitId"
          AND event."OpportunityRecommendationId" = recommendation_id
          AND event."SourceType" = source_kind
          AND event."SourceId" = NEW."Id"
    ) THEN
        RAISE EXCEPTION 'commercial opportunity record requires a matching append-only event';
    END IF;
    RETURN NULL;
END;
$$;


--
-- Name: nexora_require_opportunity_outbox(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_require_opportunity_outbox() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.commercial_opportunity_outbox message
        WHERE message."BusinessUnitId" = NEW."BusinessUnitId"
          AND message."OpportunityEventId" = NEW."Id"
          AND message."EventType" = NEW."EventType"
          AND message."PayloadJson" = NEW."PayloadJson"
    ) THEN
        RAISE EXCEPTION 'commercial opportunity event requires a matching outbox message';
    END IF;
    RETURN NULL;
END;
$$;


--
-- Name: nexora_source_document_purge_forward_only(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_source_document_purge_forward_only() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    old_rank int;
    new_rank int;
BEGIN
    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.business_unit_id IS DISTINCT FROM OLD.business_unit_id
       OR NEW.content_hash IS DISTINCT FROM OLD.content_hash
       OR NEW.original_file_name IS DISTINCT FROM OLD.original_file_name
       OR NEW.byte_size IS DISTINCT FROM OLD.byte_size
       OR NEW.created_on IS DISTINCT FROM OLD.created_on THEN
        RAISE EXCEPTION 'Source document lineage is immutable across a byte purge'
            USING ERRCODE = '23514';
    END IF;

    IF OLD.purge_state IS DISTINCT FROM 'Present'
       AND (NEW.object_bucket IS DISTINCT FROM OLD.object_bucket
            OR NEW.object_key IS DISTINCT FROM OLD.object_key
            OR NEW.object_version IS DISTINCT FROM OLD.object_version) THEN
        RAISE EXCEPTION 'Purged source object identity is immutable'
            USING ERRCODE = '23514';
    END IF;

    old_rank := CASE OLD.purge_state
        WHEN 'Present' THEN 0 WHEN 'PurgeRequested' THEN 1 WHEN 'Purged' THEN 2 END;
    new_rank := CASE NEW.purge_state
        WHEN 'Present' THEN 0 WHEN 'PurgeRequested' THEN 1 WHEN 'Purged' THEN 2 END;
    IF new_rank IS NULL THEN
        RAISE EXCEPTION 'Unknown source document purge state %', NEW.purge_state
            USING ERRCODE = '23514';
    END IF;
    IF new_rank < old_rank THEN
        RAISE EXCEPTION 'Source document purge state cannot move backwards from % to %',
            OLD.purge_state, NEW.purge_state USING ERRCODE = '23514';
    END IF;

    IF OLD.bytes_purged_on IS NOT NULL
       AND NEW.bytes_purged_on IS DISTINCT FROM OLD.bytes_purged_on THEN
        RAISE EXCEPTION 'A recorded byte purge timestamp is immutable'
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END; $$;


--
-- Name: nexora_treasury_guard_adjustment(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_adjustment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE line_amount numeric(18,2); DECLARE allocated numeric(18,2); DECLARE actor_id text;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'bank adjustments cannot be deleted' USING ERRCODE = '55000'; END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."Version" <> 1 OR NEW."JournalEntryId" IS NOT NULL
           OR NEW."ReversalJournalEntryId" IS NOT NULL
           OR (actor_id IS NOT NULL AND NEW."PreparedBy" <> actor_id) THEN
            RAISE EXCEPTION 'bank adjustments must begin as unposted drafts' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
           OR NEW."BankAccountId" <> OLD."BankAccountId" OR NEW."BankStatementLineId" <> OLD."BankStatementLineId"
           OR NEW."AccountingPeriodId" <> OLD."AccountingPeriodId" OR NEW."AccountingDate" <> OLD."AccountingDate"
           OR NEW."AdjustmentType" <> OLD."AdjustmentType" OR NEW."Description" <> OLD."Description"
           OR NEW."Amount" <> OLD."Amount" OR NEW."EvidenceReference" <> OLD."EvidenceReference"
           OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
           OR NEW."PreparedBy" <> OLD."PreparedBy" OR NEW."PreparedOn" <> OLD."PreparedOn"
           OR ((OLD."Status", NEW."Status") <> ('Draft','InReview') AND
               (NEW."SubmittedBy" IS DISTINCT FROM OLD."SubmittedBy" OR NEW."SubmittedOn" IS DISTINCT FROM OLD."SubmittedOn"))
           OR ((OLD."Status", NEW."Status") <> ('InReview','Posted') AND
               (NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                OR NEW."JournalEntryId" IS DISTINCT FROM OLD."JournalEntryId"
                OR NEW."BankJournalEntryLineId" IS DISTINCT FROM OLD."BankJournalEntryLineId"))
           OR ((OLD."Status", NEW."Status") <> ('InReview','Rejected') AND
               (NEW."RejectedBy" IS DISTINCT FROM OLD."RejectedBy" OR NEW."RejectedOn" IS DISTINCT FROM OLD."RejectedOn"
                OR NEW."RejectionReason" IS DISTINCT FROM OLD."RejectionReason"))
           OR ((OLD."Status", NEW."Status") <> ('Draft','Cancelled') AND
               (NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
                OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"))
           OR ((OLD."Status", NEW."Status") <> ('Posted','Reversed') AND
               (NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
                OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
                OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference"
                OR NEW."ReversalJournalEntryId" IS DISTINCT FROM OLD."ReversalJournalEntryId"
                OR NEW."ReversalBankJournalEntryLineId" IS DISTINCT FROM OLD."ReversalBankJournalEntryLineId"))
           OR NEW."Version" <> OLD."Version" + 1 THEN
            RAISE EXCEPTION 'bank adjustment accounting content is immutable' USING ERRCODE = '55000';
        END IF;
        IF OLD."Status" = 'Draft' AND NEW."Status" = 'InReview' THEN
            IF NEW."SubmittedBy" IS NULL OR NEW."SubmittedOn" IS NULL
               OR (actor_id IS NOT NULL AND NEW."SubmittedBy" <> actor_id) THEN RAISE EXCEPTION 'adjustment submission identity is required' USING ERRCODE = '23514'; END IF;
        ELSIF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
            IF NEW."CancelledBy" IS NULL OR length(trim(NEW."CancellationReason")) < 20
               OR (actor_id IS NOT NULL AND NEW."CancelledBy" <> actor_id) THEN RAISE EXCEPTION 'adjustment cancellation evidence is required' USING ERRCODE = '23514'; END IF;
        ELSIF OLD."Status" = 'InReview' AND NEW."Status" = 'Rejected' THEN
            IF NEW."RejectedBy" IS NULL OR lower(trim(NEW."RejectedBy")) IN (lower(trim(NEW."PreparedBy")),lower(trim(NEW."SubmittedBy"))) OR length(trim(NEW."RejectionReason")) < 20
               OR (actor_id IS NOT NULL AND NEW."RejectedBy" <> actor_id) THEN RAISE EXCEPTION 'independent adjustment rejection evidence is required' USING ERRCODE = '23514'; END IF;
        ELSIF OLD."Status" = 'InReview' AND NEW."Status" = 'Posted' THEN
            IF NEW."ApprovedBy" IS NULL OR lower(trim(NEW."ApprovedBy")) IN (lower(trim(NEW."PreparedBy")),lower(trim(NEW."SubmittedBy")))
               OR (actor_id IS NOT NULL AND NEW."ApprovedBy" <> actor_id)
               OR NEW."JournalEntryId" IS NULL OR NEW."BankJournalEntryLineId" IS NULL THEN
                RAISE EXCEPTION 'independent adjustment approval and journal evidence are required' USING ERRCODE = '23514';
            END IF;
            SELECT abs(line."SignedAmount") INTO STRICT line_amount FROM public."BankStatementLines" line
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId" AND line."Id" = NEW."BankStatementLineId" FOR UPDATE;
            SELECT COALESCE(sum(other."Amount"),0) INTO allocated FROM public."BankAdjustments" other
                WHERE other."BusinessUnitId" = NEW."BusinessUnitId" AND other."BankStatementLineId" = NEW."BankStatementLineId"
                  AND other."Id" <> NEW."Id" AND other."Status" = 'Posted';
            IF allocated + NEW."Amount" > line_amount THEN RAISE EXCEPTION 'posted adjustments over-allocate the statement line' USING ERRCODE = '23514'; END IF;
        ELSIF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
            IF NEW."ReversedBy" IS NULL OR lower(trim(NEW."ReversedBy")) IN (lower(trim(NEW."PreparedBy")),lower(trim(NEW."ApprovedBy")))
               OR (actor_id IS NOT NULL AND NEW."ReversedBy" <> actor_id)
               OR length(trim(NEW."ReversalReason")) < 20 OR length(trim(NEW."ReversalEvidenceReference")) < 8
               OR NEW."ReversalJournalEntryId" IS NULL OR NEW."ReversalBankJournalEntryLineId" IS NULL THEN
                RAISE EXCEPTION 'independent adjustment reversal evidence is required' USING ERRCODE = '23514';
            END IF;
        ELSE RAISE EXCEPTION 'invalid bank-adjustment transition' USING ERRCODE = '55000';
        END IF;
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_treasury_guard_distribution(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_distribution() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'bank adjustment distributions are append-only' USING ERRCODE = '55000'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public."BankAdjustments" adjustment
        WHERE adjustment."BusinessUnitId" = NEW."BusinessUnitId" AND adjustment."Id" = NEW."BankAdjustmentId"
          AND adjustment."Status" = 'Draft') THEN
        RAISE EXCEPTION 'distributions can only be added to a draft adjustment' USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_treasury_guard_rule(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_rule() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $_$
DECLARE canonical text; DECLARE actor_id text;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'bank matching rules cannot be deleted' USING ERRCODE = '55000'; END IF;
    IF current_setting('role', true) = 'nexora_tenant_app' THEN
        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
    END IF;
    IF TG_OP = 'INSERT' THEN
        canonical := COALESCE(NEW."BankAccountId"::text, '*') || '|' || NEW."Code" || '|'
            || NEW."RuleVersion"::text || '|' || NEW."Name" || '|' || NEW."EvaluatorType" || '|'
            || NEW."Priority"::text || '|' || NEW."AmountTolerance"::text || '|'
            || NEW."BookingDateToleranceDays"::text || '|' || NEW."ReferenceMode" || '|'
            || lower(NEW."RequireUniquePair"::text);
        IF NEW."Code" !~ '^[A-Z][A-Z0-9_]{2,79}$'
           OR NEW."DefinitionHash" <> encode(digest(convert_to(canonical,'UTF8'),'sha256'),'hex') THEN
            RAISE EXCEPTION 'matching-rule code or canonical definition hash is invalid' USING ERRCODE = '23514';
        END IF;
        IF (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id) OR NOT ((NEW."Status" = 'Draft' AND NEW."RecordVersion" = 1
              AND NEW."ApprovedBy" IS NULL AND NEW."ActivatedBy" IS NULL AND NEW."RetiredBy" IS NULL)
            OR (NEW."Status" = 'Active' AND NEW."CreatedBy" = 'system:bank-rule-bootstrap'
              AND NEW."ApprovedBy" = 'system:bank-rule-bootstrap' AND NEW."ActivatedBy" = 'system:bank-rule-bootstrap')) THEN
            RAISE EXCEPTION 'invalid initial matching-rule state' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
           OR NEW."BankAccountId" IS DISTINCT FROM OLD."BankAccountId" OR NEW."Code" <> OLD."Code"
           OR NEW."RuleVersion" <> OLD."RuleVersion" OR NEW."Name" <> OLD."Name"
           OR NEW."EvaluatorType" <> OLD."EvaluatorType" OR NEW."Priority" <> OLD."Priority"
           OR NEW."AmountTolerance" <> OLD."AmountTolerance"
           OR NEW."BookingDateToleranceDays" <> OLD."BookingDateToleranceDays"
           OR NEW."ReferenceMode" <> OLD."ReferenceMode" OR NEW."RequireUniquePair" <> OLD."RequireUniquePair"
           OR NEW."DefinitionHash" <> OLD."DefinitionHash" OR NEW."SupersedesRuleId" IS DISTINCT FROM OLD."SupersedesRuleId"
           OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
           OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
           OR NEW."RecordVersion" <> OLD."RecordVersion" + 1
           OR NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'Approved'
                    AND NEW."ApprovedBy" IS NOT NULL AND NEW."ApprovedOn" IS NOT NULL
                    AND lower(trim(NEW."ApprovedBy")) <> lower(trim(NEW."CreatedBy"))
                    AND (actor_id IS NULL OR NEW."ApprovedBy" = actor_id)
                    AND NEW."ActivatedBy" IS NOT DISTINCT FROM OLD."ActivatedBy"
                    AND NEW."ActivatedOn" IS NOT DISTINCT FROM OLD."ActivatedOn"
                    AND NEW."RetiredBy" IS NOT DISTINCT FROM OLD."RetiredBy"
                    AND NEW."RetiredOn" IS NOT DISTINCT FROM OLD."RetiredOn")
                OR (OLD."Status" = 'Approved' AND NEW."Status" = 'Active'
                    AND NEW."ApprovedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
                    AND NEW."ApprovedOn" IS NOT DISTINCT FROM OLD."ApprovedOn"
                    AND NEW."ActivatedBy" IS NOT NULL AND NEW."ActivatedOn" IS NOT NULL
                    AND lower(trim(NEW."ActivatedBy")) <> lower(trim(NEW."CreatedBy"))
                    AND (actor_id IS NULL OR NEW."ActivatedBy" = actor_id)
                    AND NEW."RetiredBy" IS NOT DISTINCT FROM OLD."RetiredBy"
                    AND NEW."RetiredOn" IS NOT DISTINCT FROM OLD."RetiredOn")
                OR (OLD."Status" = 'Active' AND NEW."Status" = 'Retired'
                    AND NEW."ApprovedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
                    AND NEW."ApprovedOn" IS NOT DISTINCT FROM OLD."ApprovedOn"
                    AND NEW."ActivatedBy" IS NOT DISTINCT FROM OLD."ActivatedBy"
                    AND NEW."ActivatedOn" IS NOT DISTINCT FROM OLD."ActivatedOn"
                    AND NEW."RetiredBy" IS NOT NULL AND NEW."RetiredOn" IS NOT NULL
                    AND (actor_id IS NULL OR NEW."RetiredBy" = actor_id)))
           OR length(trim(NEW."LifecycleReason")) < 20 OR length(trim(NEW."EvidenceReference")) < 8 THEN
            RAISE EXCEPTION 'matching-rule definitions are immutable and transitions require independent evidence' USING ERRCODE = '55000';
        END IF;
    END IF;
    RETURN NEW;
END $_$;


--
-- Name: nexora_treasury_guard_snapshot(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_snapshot() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'reconciliation rule snapshots are append-only' USING ERRCODE = '55000'; END IF;
    IF NOT EXISTS (SELECT 1 FROM public."BankMatchingRules" rule
        JOIN public."ReconciliationRuns" run ON run."BusinessUnitId" = rule."BusinessUnitId"
          AND run."Id" = NEW."ReconciliationRunId"
        WHERE rule."BusinessUnitId" = NEW."BusinessUnitId" AND rule."Id" = NEW."BankMatchingRuleId"
          AND rule."DefinitionHash" = NEW."DefinitionHash" AND rule."Status" = 'Active'
          AND (rule."BankAccountId" IS NULL OR rule."BankAccountId" = run."BankAccountId")) THEN
        RAISE EXCEPTION 'rule snapshot must preserve an active applicable immutable definition' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_treasury_validate_adjustment(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_adjustment() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE current_row public."BankAdjustments"%ROWTYPE; DECLARE distribution_total numeric(18,2);
DECLARE bank_signed numeric(18,2); DECLARE bank_ledger bigint; DECLARE mismatch integer;
DECLARE distribution_count integer; DECLARE journal_line_count integer;
BEGIN
    SELECT * INTO current_row FROM public."BankAdjustments" WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "Id" = NEW."Id";
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT COALESCE(sum("Amount"),0) INTO distribution_total FROM public."BankAdjustmentDistributions"
        WHERE "BusinessUnitId" = current_row."BusinessUnitId" AND "BankAdjustmentId" = current_row."Id";
    IF distribution_total <> current_row."Amount" THEN RAISE EXCEPTION 'adjustment distributions must equal the adjustment amount' USING ERRCODE = '23514'; END IF;
    IF current_row."Status" IN ('Posted','Reversed') THEN
        SELECT line."SignedAmount", account."LedgerAccountId" INTO STRICT bank_signed, bank_ledger
        FROM public."BankStatementLines" line JOIN public."BankAccounts" account
          ON account."BusinessUnitId" = line."BusinessUnitId" AND account."Id" = line."BankAccountId"
        WHERE line."BusinessUnitId" = current_row."BusinessUnitId" AND line."Id" = current_row."BankStatementLineId"
          AND account."Id" = current_row."BankAccountId";
        SELECT count(*) INTO mismatch FROM public."JournalEntries" journal
        WHERE journal."BusinessUnitId" = current_row."BusinessUnitId" AND journal."Id" = current_row."JournalEntryId"
          AND journal."Status" IN ('Posted','Reversed') AND journal."SourceType" = 'BankAdjustment'
          AND journal."SourceReference" = current_row."Id"::text
          AND journal."SourceVersion" = CASE WHEN current_row."Status" = 'Posted'
              THEN current_row."Version" - 1 ELSE current_row."Version" - 2 END
          AND journal."TotalDebit" = current_row."Amount" AND journal."TotalCredit" = current_row."Amount";
        SELECT count(*) INTO distribution_count FROM public."BankAdjustmentDistributions" distribution
            WHERE distribution."BusinessUnitId" = current_row."BusinessUnitId"
              AND distribution."BankAdjustmentId" = current_row."Id";
        SELECT count(*) INTO journal_line_count FROM public."JournalEntryLines" jl
            WHERE jl."BusinessUnitId" = current_row."BusinessUnitId"
              AND jl."JournalEntryId" = current_row."JournalEntryId";
        IF mismatch <> 1 OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" jl
            WHERE jl."BusinessUnitId" = current_row."BusinessUnitId" AND jl."Id" = current_row."BankJournalEntryLineId"
              AND jl."JournalEntryId" = current_row."JournalEntryId" AND jl."LedgerAccountId" = bank_ledger
              AND jl."Sequence" = 1 AND jl."SourceReference" = 'BADJ:' || current_row."Id"::text || ':BANK'
              AND ((bank_signed > 0 AND jl."FunctionalDebit" = current_row."Amount" AND jl."FunctionalCredit" = 0)
                OR (bank_signed < 0 AND jl."FunctionalCredit" = current_row."Amount" AND jl."FunctionalDebit" = 0)))
           OR journal_line_count <> distribution_count + 1
           OR EXISTS (SELECT 1 FROM public."BankAdjustmentDistributions" distribution
                LEFT JOIN public."LedgerAccounts" account
                  ON account."BusinessUnitId" = distribution."BusinessUnitId"
                 AND account."Id" = distribution."LedgerAccountId"
                WHERE distribution."BusinessUnitId" = current_row."BusinessUnitId"
                  AND distribution."BankAdjustmentId" = current_row."Id"
                  AND (account."Id" IS NULL OR account."IsActive" IS NOT TRUE
                    OR account."IsControlAccount" IS TRUE OR account."Id" = bank_ledger
                    OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" jl
                        WHERE jl."BusinessUnitId" = current_row."BusinessUnitId"
                          AND jl."JournalEntryId" = current_row."JournalEntryId"
                          AND jl."Sequence" = distribution."Sequence" + 1
                          AND jl."LedgerAccountId" = distribution."LedgerAccountId"
                          AND jl."SourceReference" = 'BADJ:' || current_row."Id"::text || ':DIST:' || distribution."Sequence"::text
                          AND ((bank_signed > 0 AND jl."FunctionalDebit" = 0 AND jl."FunctionalCredit" = distribution."Amount")
                            OR (bank_signed < 0 AND jl."FunctionalDebit" = distribution."Amount" AND jl."FunctionalCredit" = 0))))) THEN
            RAISE EXCEPTION 'posted adjustment journal does not match immutable treasury evidence' USING ERRCODE = '23514';
        END IF;
        IF current_row."Status" = 'Reversed' AND (NOT EXISTS (SELECT 1 FROM public."JournalEntries" reversal
                WHERE reversal."BusinessUnitId" = current_row."BusinessUnitId"
                  AND reversal."Id" = current_row."ReversalJournalEntryId"
                  AND reversal."ReversesJournalEntryId" = current_row."JournalEntryId"
                  AND reversal."SourceType" = 'JournalReversal' AND reversal."Status" = 'Posted'
                  AND reversal."TotalDebit" = current_row."Amount" AND reversal."TotalCredit" = current_row."Amount")
            OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" reversal_line
                WHERE reversal_line."BusinessUnitId" = current_row."BusinessUnitId"
                  AND reversal_line."Id" = current_row."ReversalBankJournalEntryLineId"
                  AND reversal_line."JournalEntryId" = current_row."ReversalJournalEntryId"
                  AND reversal_line."LedgerAccountId" = bank_ledger
                  AND ((bank_signed > 0 AND reversal_line."FunctionalDebit" = 0
                        AND reversal_line."FunctionalCredit" = current_row."Amount")
                    OR (bank_signed < 0 AND reversal_line."FunctionalDebit" = current_row."Amount"
                        AND reversal_line."FunctionalCredit" = 0)))) THEN
            RAISE EXCEPTION 'reversed adjustment requires an exact posted journal reversal' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END $$;


--
-- Name: nexora_treasury_validate_cash_bridge(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_cash_bridge() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE payment public."CustomerPayments"%ROWTYPE; DECLARE refund public."CustomerRefunds"%ROWTYPE;
DECLARE bank_ledger bigint; DECLARE ar_ledger bigint; DECLARE unapplied_ledger bigint;
DECLARE bank_currency bigint; DECLARE bank_ledger_currency bigint; DECLARE book_currency bigint;
DECLARE allocated numeric(18,2); DECLARE unapplied numeric(18,2); DECLARE line_count integer;
DECLARE expected_line_count integer; DECLARE expected_source_version bigint; DECLARE unapplied_sequence integer;
BEGIN
    IF TG_TABLE_NAME = 'CustomerPayments' THEN
        SELECT * INTO payment FROM public."CustomerPayments" WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "Id" = NEW."Id";
        IF NOT FOUND THEN RETURN NULL; END IF;
        IF payment."AccountingBridgeRequired" IS FALSE THEN RETURN NULL; END IF;
        IF payment."BankAccountId" IS NULL OR payment."JournalEntryId" IS NULL THEN
            RAISE EXCEPTION 'new customer payments require durable bank and journal evidence' USING ERRCODE = '23514';
        END IF;
        SELECT bank."LedgerAccountId", bank."CurrencyId", ledger."CurrencyId"
            INTO STRICT bank_ledger, bank_currency, bank_ledger_currency
            FROM public."BankAccounts" bank JOIN public."LedgerAccounts" ledger
              ON ledger."BusinessUnitId" = bank."BusinessUnitId" AND ledger."Id" = bank."LedgerAccountId"
            WHERE bank."BusinessUnitId" = payment."BusinessUnitId" AND bank."Id" = payment."BankAccountId";
        SELECT "ReceivablesControlAccountId", "UnappliedCashAccountId", "FunctionalCurrencyId"
            INTO STRICT ar_ledger, unapplied_ledger, book_currency
            FROM public."LedgerBooks" WHERE "BusinessUnitId" = payment."BusinessUnitId";
        SELECT COALESCE(sum("Amount"),0) INTO allocated FROM public."PaymentAllocations"
            WHERE "BusinessUnitId" = payment."BusinessUnitId" AND "CustomerPaymentId" = payment."Id";
        unapplied := payment."Amount" - allocated;
        expected_line_count := 1;
        unapplied_sequence := 2;
        IF allocated > 0 THEN expected_line_count := expected_line_count + 1; END IF;
        IF allocated > 0 THEN unapplied_sequence := 3; END IF;
        IF unapplied > 0 THEN expected_line_count := expected_line_count + 1; END IF;
        expected_source_version := payment."Version";
        IF payment."Status" = 'Reversed' THEN expected_source_version := expected_source_version - 1; END IF;
        SELECT count(*) INTO line_count FROM public."JournalEntryLines" line
            WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId";
        IF payment."CurrencyId" <> bank_currency OR payment."CurrencyId" <> bank_ledger_currency
           OR payment."CurrencyId" <> book_currency
           OR NOT EXISTS (SELECT 1 FROM public."JournalEntries" journal WHERE journal."BusinessUnitId" = payment."BusinessUnitId"
            AND journal."Id" = payment."JournalEntryId" AND journal."SourceType" = 'CustomerPayment'
            AND journal."SourceReference" = payment."Id"::text AND journal."Status" IN ('Posted','Reversed')
            AND journal."SourceVersion" = expected_source_version AND journal."FunctionalCurrencyId" = payment."CurrencyId"
            AND journal."TotalDebit" = payment."Amount" AND journal."TotalCredit" = payment."Amount")
           OR allocated < 0 OR allocated > payment."Amount"
           OR line_count <> expected_line_count
           OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
            WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId"
              AND line."Sequence" = 1 AND line."LedgerAccountId" = bank_ledger
              AND line."SourceReference" = 'PAY:' || payment."Id"::text || ':BANK'
              AND line."TransactionCurrencyId" = payment."CurrencyId" AND line."ExchangeRate" = 1
              AND line."TransactionDebit" = payment."Amount" AND line."TransactionCredit" = 0
              AND line."FunctionalDebit" = payment."Amount" AND line."FunctionalCredit" = 0)
           OR (allocated > 0 AND NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId"
                  AND line."Sequence" = 2 AND line."LedgerAccountId" = ar_ledger
                  AND line."SourceReference" = 'PAY:' || payment."Id"::text || ':AR'
                  AND line."TransactionCurrencyId" = payment."CurrencyId" AND line."ExchangeRate" = 1
                  AND line."TransactionDebit" = 0 AND line."TransactionCredit" = allocated
                  AND line."FunctionalDebit" = 0 AND line."FunctionalCredit" = allocated))
           OR (unapplied > 0 AND NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId"
                  AND line."Sequence" = unapplied_sequence
                  AND line."LedgerAccountId" = unapplied_ledger
                  AND line."SourceReference" = 'PAY:' || payment."Id"::text || ':UNAPPLIED'
                  AND line."TransactionCurrencyId" = payment."CurrencyId" AND line."ExchangeRate" = 1
                  AND line."TransactionDebit" = 0 AND line."TransactionCredit" = unapplied
                  AND line."FunctionalDebit" = 0 AND line."FunctionalCredit" = unapplied)) THEN
            RAISE EXCEPTION 'customer payment journal provenance is invalid' USING ERRCODE = '23514';
        END IF;
        IF payment."Status" = 'Reversed' AND (payment."ReversalJournalEntryId" IS NULL OR NOT EXISTS
            (SELECT 1 FROM public."JournalEntries" reversal WHERE reversal."BusinessUnitId" = payment."BusinessUnitId"
              AND reversal."Id" = payment."ReversalJournalEntryId" AND reversal."ReversesJournalEntryId" = payment."JournalEntryId"
              AND reversal."Status" = 'Posted' AND reversal."FunctionalCurrencyId" = payment."CurrencyId")) THEN
            RAISE EXCEPTION 'reversed customer payment requires an exact posted journal reversal' USING ERRCODE = '23514';
        END IF;
    ELSE
        SELECT * INTO refund FROM public."CustomerRefunds" WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "Id" = NEW."Id";
        IF NOT FOUND OR refund."PostingStatus" <> 'Settled' THEN RETURN NULL; END IF;
        IF refund."BankAccountId" IS NULL OR refund."JournalEntryId" IS NULL THEN
            RAISE EXCEPTION 'settled refunds require durable bank and journal evidence' USING ERRCODE = '23514';
        END IF;
        SELECT bank."LedgerAccountId", bank."CurrencyId", ledger."CurrencyId"
            INTO STRICT bank_ledger, bank_currency, bank_ledger_currency
            FROM public."BankAccounts" bank JOIN public."LedgerAccounts" ledger
              ON ledger."BusinessUnitId" = bank."BusinessUnitId" AND ledger."Id" = bank."LedgerAccountId"
            WHERE bank."BusinessUnitId" = refund."BusinessUnitId" AND bank."Id" = refund."BankAccountId";
        SELECT "UnappliedCashAccountId", "FunctionalCurrencyId" INTO STRICT unapplied_ledger, book_currency FROM public."LedgerBooks"
            WHERE "BusinessUnitId" = refund."BusinessUnitId";
        SELECT count(*) INTO line_count FROM public."JournalEntryLines" line
            WHERE line."BusinessUnitId" = refund."BusinessUnitId" AND line."JournalEntryId" = refund."JournalEntryId";
        IF refund."CurrencyId" <> bank_currency OR refund."CurrencyId" <> bank_ledger_currency
           OR refund."CurrencyId" <> book_currency
           OR NOT EXISTS (SELECT 1 FROM public."JournalEntries" journal WHERE journal."BusinessUnitId" = refund."BusinessUnitId"
            AND journal."Id" = refund."JournalEntryId" AND journal."SourceType" = 'CustomerRefund'
            AND journal."SourceReference" = refund."Id"::text AND journal."Status" = 'Posted'
            AND journal."SourceVersion" = refund."Version" AND journal."FunctionalCurrencyId" = refund."CurrencyId"
            AND journal."TotalDebit" = refund."Amount" AND journal."TotalCredit" = refund."Amount")
           OR line_count <> 2
           OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
            WHERE line."BusinessUnitId" = refund."BusinessUnitId" AND line."JournalEntryId" = refund."JournalEntryId"
              AND line."Sequence" = 1 AND line."LedgerAccountId" = unapplied_ledger
              AND line."SourceReference" = 'REF:' || refund."Id"::text || ':UNAPPLIED'
              AND line."TransactionCurrencyId" = refund."CurrencyId" AND line."ExchangeRate" = 1
              AND line."TransactionDebit" = refund."Amount" AND line."TransactionCredit" = 0
              AND line."FunctionalDebit" = refund."Amount" AND line."FunctionalCredit" = 0)
           OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
            WHERE line."BusinessUnitId" = refund."BusinessUnitId" AND line."JournalEntryId" = refund."JournalEntryId"
              AND line."Sequence" = 2 AND line."LedgerAccountId" = bank_ledger
              AND line."SourceReference" = 'REF:' || refund."Id"::text || ':BANK'
              AND line."TransactionCurrencyId" = refund."CurrencyId" AND line."ExchangeRate" = 1
              AND line."TransactionCredit" = refund."Amount" AND line."TransactionDebit" = 0
              AND line."FunctionalCredit" = refund."Amount" AND line."FunctionalDebit" = 0) THEN
            RAISE EXCEPTION 'customer refund journal provenance is invalid' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END; $$;


--
-- Name: nexora_treasury_validate_match_rule(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_match_rule() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (NEW."BankMatchingRuleId" IS DISTINCT FROM OLD."BankMatchingRuleId"
       OR NEW."RuleDefinitionHash" IS DISTINCT FROM OLD."RuleDefinitionHash") THEN
        RAISE EXCEPTION 'matching-rule provenance is immutable' USING ERRCODE = '55000';
    END IF;
    IF NEW."MatchType" = 'DeterministicExact' AND NOT EXISTS (
        SELECT 1 FROM public."BankMatchingRules" rule
        JOIN public."ReconciliationRunRules" snapshot ON snapshot."BusinessUnitId" = rule."BusinessUnitId"
          AND snapshot."BankMatchingRuleId" = rule."Id" AND snapshot."ReconciliationRunId" = NEW."ReconciliationRunId"
        WHERE rule."BusinessUnitId" = NEW."BusinessUnitId" AND rule."Id" = NEW."BankMatchingRuleId"
          AND rule."Code" = NEW."RuleCode" AND rule."RuleVersion" = NEW."RuleVersion"
          AND rule."DefinitionHash" = NEW."RuleDefinitionHash" AND snapshot."DefinitionHash" = rule."DefinitionHash") THEN
        RAISE EXCEPTION 'deterministic match must reference a snapshotted rule definition' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_treasury_validate_run_rules(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_run_rules() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE canonical text;
BEGIN
    SELECT string_agg(snapshot."DefinitionHash", '|' ORDER BY snapshot."EvaluationOrder") INTO canonical
    FROM public."ReconciliationRunRules" snapshot WHERE snapshot."BusinessUnitId" = NEW."BusinessUnitId"
      AND snapshot."ReconciliationRunId" = NEW."Id";
    IF canonical IS NULL OR encode(digest(convert_to(canonical,'UTF8'),'sha256'),'hex') <> NEW."RuleSetHash" THEN
        RAISE EXCEPTION 'reconciliation requires a complete immutable rule-set snapshot' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $$;


--
-- Name: nexora_validate_commercial_line_resolution(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_commercial_line_resolution() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public."Leads" l
        WHERE l."ID" = NEW."LeadId" AND l."BusinessUnitID" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'resolution lead must belong to the same tenant';
    END IF;
    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."Products" p
        WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'resolution product must belong to the same tenant';
    END IF;
    IF NEW."RfqId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."RFQ" r
        WHERE r."ID" = NEW."RfqId" AND r."BusinessUnitID" = NEW."BusinessUnitId"
          AND r."LeadID" = NEW."LeadId") THEN
        RAISE EXCEPTION 'resolution RFQ must belong to the same tenant and lead';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_validate_downstream_commercial_identity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_downstream_commercial_identity() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD."CommercialCaseID" IS NOT NULL
       AND (NEW."CommercialCaseID", NEW."NexoraSerial") IS DISTINCT FROM
           (OLD."CommercialCaseID", OLD."NexoraSerial") THEN
        RAISE EXCEPTION 'Nexora Serial lineage is immutable once assigned' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
        RAISE EXCEPTION 'Commercial customer identity is immutable once assigned' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
        RAISE EXCEPTION 'Commercial contact identity is immutable once assigned' USING ERRCODE = '55000';
    END IF;
    IF NEW."CommercialCaseID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "CommercialCases" commercial_case
        WHERE commercial_case."BusinessUnitID" = NEW."BusinessUnitID"
          AND commercial_case."Id" = NEW."CommercialCaseID"
          AND commercial_case."MasterReference" = NEW."NexoraSerial") THEN
        RAISE EXCEPTION 'Nexora Serial must match the tenant commercial case' USING ERRCODE = '23503';
    END IF;
    IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Customers" customer
        WHERE customer."ID" = NEW."CustomerID"
          AND customer."BUID" = NEW."BusinessUnitID") THEN
        RAISE EXCEPTION 'Commercial customer must belong to the same tenant' USING ERRCODE = '23503';
    END IF;
    IF NEW."ContactID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Contacts" contact
        WHERE contact."ID" = NEW."ContactID"
          AND contact."CustomerID" = NEW."CustomerID") THEN
        RAISE EXCEPTION 'Commercial contact must belong to the assigned customer' USING ERRCODE = '23503';
    END IF;
    IF TG_TABLE_NAME = 'RFQ' AND (to_jsonb(NEW)->>'LeadID') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Leads" lead
        WHERE lead."ID" = (to_jsonb(NEW)->>'LeadID')::bigint
          AND lead."BusinessUnitID" = NEW."BusinessUnitID"
          AND (lead."CommercialCaseId", lead."CommercialCaseReference", lead."CustomerID", lead."ContactID")
              IS NOT DISTINCT FROM
              (NEW."CommercialCaseID", NEW."NexoraSerial", NEW."CustomerID", NEW."ContactID")) THEN
        RAISE EXCEPTION 'RFQ commercial identity must match its Lead' USING ERRCODE = '23503';
    END IF;
    IF TG_TABLE_NAME = 'Quotes' AND (to_jsonb(NEW)->>'RFQID') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "RFQ" rfq
        WHERE rfq."ID" = (to_jsonb(NEW)->>'RFQID')::bigint
          AND rfq."BusinessUnitID" = NEW."BusinessUnitID"
          AND (rfq."CommercialCaseID", rfq."NexoraSerial", rfq."CustomerID", rfq."ContactID")
              IS NOT DISTINCT FROM
              (NEW."CommercialCaseID", NEW."NexoraSerial", NEW."CustomerID", NEW."ContactID")) THEN
        RAISE EXCEPTION 'Quote commercial identity must match its RFQ' USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_validate_inventory_tenant(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_inventory_tenant() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_TABLE_NAME = 'Inventory' THEN
        IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."Buid") THEN
            RAISE EXCEPTION 'inventory product must belong to the same tenant';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_TABLE_NAME = 'product_supersessions' THEN
        IF NOT EXISTS (SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."SupersededProductId" AND p."BUID" = NEW."BusinessUnitId")
           OR NOT EXISTS (SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."ReplacementProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
            RAISE EXCEPTION 'supersession products must belong to the same tenant';
        END IF;
        RETURN NEW;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM public."Products" p WHERE p."ID" = NEW."ProductId" AND p."BUID" = NEW."BusinessUnitId") THEN
        RAISE EXCEPTION 'product must belong to the same tenant';
    END IF;
    IF TG_TABLE_NAME IN ('incoming_inventory', 'inventory_movements') THEN
        IF NOT EXISTS (SELECT 1 FROM public."Warehouses" w WHERE w."ID" = NEW."WarehouseId" AND w."BusinessUnitID" = NEW."BusinessUnitId") THEN
            RAISE EXCEPTION 'warehouse must belong to the same tenant';
        END IF;
        IF NEW."InventoryId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public."Inventory" i WHERE i."Id" = NEW."InventoryId" AND i."Buid" = NEW."BusinessUnitId") THEN
            RAISE EXCEPTION 'stock row must belong to the same tenant';
        END IF;
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_validate_inventory_warehouse_tenant(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_inventory_warehouse_tenant() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."WarehouseId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM public."Warehouses" w
        WHERE w."ID" = NEW."WarehouseId" AND w."BusinessUnitID" = NEW."Buid") THEN
        RAISE EXCEPTION 'inventory warehouse must belong to the same tenant';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: nexora_validate_lead_commercial_identity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_lead_commercial_identity() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
        RAISE EXCEPTION 'Lead customer identity is immutable once resolved' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
        RAISE EXCEPTION 'Lead contact identity is immutable once resolved' USING ERRCODE = '55000';
    END IF;
    IF NEW."CustomerID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Customers" customer
        WHERE customer."ID" = NEW."CustomerID"
          AND customer."BUID" = NEW."BusinessUnitID") THEN
        RAISE EXCEPTION 'Lead customer must belong to the same tenant' USING ERRCODE = '23503';
    END IF;
    IF NEW."ContactID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Contacts" contact
        WHERE contact."ID" = NEW."ContactID"
          AND contact."CustomerID" = NEW."CustomerID") THEN
        RAISE EXCEPTION 'Lead contact must belong to the resolved customer' USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_validate_learning_governance_insert(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_learning_governance_insert() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'pg_catalog', 'public'
    AS $$
BEGIN
    IF NEW."Action" = 'ROLLED_BACK'
       AND NEW."RevertsVersion" IS DISTINCT FROM NEW."Version" - 1 THEN
        RAISE EXCEPTION 'rollback must compensate the immediately preceding version'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_validate_opportunity_feedback(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_opportunity_feedback() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."SupersedesFeedbackId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM public.commercial_opportunity_feedback prior
        WHERE prior."BusinessUnitId" = NEW."BusinessUnitId"
          AND prior."Id" = NEW."SupersedesFeedbackId"
          AND prior."OpportunityRecommendationId" = NEW."OpportunityRecommendationId"
    ) THEN
        RAISE EXCEPTION 'superseded feedback must belong to the same recommendation';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_validate_opportunity_outcome(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_opportunity_outcome() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE recommendation_created timestamp without time zone;
BEGIN
    SELECT recommendation."GeneratedAtUtc" INTO recommendation_created
    FROM public.commercial_opportunity_recommendations recommendation
    WHERE recommendation."BusinessUnitId" = NEW."BusinessUnitId"
      AND recommendation."Id" = NEW."OpportunityRecommendationId";

    IF NEW."ObservedAtUtc" <= recommendation_created THEN
        RAISE EXCEPTION 'observed outcomes must occur after the shadow recommendation';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_validate_opportunity_recommendation_lineage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_opportunity_recommendation_lineage() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public."Leads" lead
        WHERE lead."BusinessUnitID" = NEW."BusinessUnitId"
          AND lead."ID" = NEW."LeadId"
          AND lead."CommercialCaseId" = NEW."CommercialCaseId"
          AND lead."CommercialCaseReference" = NEW."NexoraSerial"
    ) THEN
        RAISE EXCEPTION 'opportunity recommendation must retain tenant-qualified lead and Nexora Serial lineage';
    END IF;

    IF NEW."SupersedesRecommendationId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM public.commercial_opportunity_recommendations prior
        WHERE prior."BusinessUnitId" = NEW."BusinessUnitId"
          AND prior."Id" = NEW."SupersedesRecommendationId"
          AND prior."CommercialCaseId" = NEW."CommercialCaseId"
          AND prior."LeadId" = NEW."LeadId"
          AND prior."NexoraSerial" = NEW."NexoraSerial"
    ) THEN
        RAISE EXCEPTION 'superseded recommendation must retain the same commercial identity';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: nexora_validate_order_commercial_identity(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_order_commercial_identity() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD."CommercialCaseID" IS NOT NULL
       AND (NEW."CommercialCaseID", NEW."NexoraSerial") IS DISTINCT FROM
           (OLD."CommercialCaseID", OLD."NexoraSerial") THEN
        RAISE EXCEPTION 'Order Nexora Serial lineage is immutable once assigned' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."CustomerID" IS NOT NULL
       AND NEW."CustomerID" IS DISTINCT FROM OLD."CustomerID" THEN
        RAISE EXCEPTION 'Order customer identity is immutable once assigned' USING ERRCODE = '55000';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD."ContactID" IS NOT NULL
       AND NEW."ContactID" IS DISTINCT FROM OLD."ContactID" THEN
        RAISE EXCEPTION 'Order contact identity is immutable once assigned' USING ERRCODE = '55000';
    END IF;
    IF NEW."QuoteID" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Quotes" quote
        WHERE quote."ID" = NEW."QuoteID"
          AND quote."BusinessUnitID" = NEW."BusinessUnitID"
          AND (quote."CommercialCaseID", quote."NexoraSerial", quote."CustomerID", quote."ContactID")
              IS NOT DISTINCT FROM
              (NEW."CommercialCaseID", NEW."NexoraSerial", NEW."CustomerID", NEW."ContactID")) THEN
        RAISE EXCEPTION 'Order commercial identity must match its Quote' USING ERRCODE = '23503';
    END IF;
    RETURN NEW;
END; $$;


--
-- Name: nexora_validate_procurement_inventory_tenant(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_procurement_inventory_tenant() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."InventoryId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Inventory" i
        WHERE i."Id" = NEW."InventoryId"
          AND i."Buid" = NEW."BusinessUnitId"
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '23503',
            MESSAGE = 'purchase-order inventory must belong to the same tenant';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_validate_procurement_product_tenant(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_validate_procurement_product_tenant() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."ProductId" IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM "Products" product
        WHERE product."ID" = NEW."ProductId"
          AND product."BUID" = NEW."BusinessUnitId"
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '23503',
            MESSAGE = 'procurement product must belong to the same tenant';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_write_finance_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, audit_action text, audit_actor text, audit_detail jsonb, occurred_on timestamp without time zone) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE aggregate_status text;
DECLARE aggregate_document_type text;
BEGIN
    IF business_unit_id <= 0 OR aggregate_id <= 0
       OR audit_actor IS NULL OR length(trim(audit_actor)) = 0
       OR audit_detail IS NULL OR jsonb_typeof(audit_detail) <> 'object'
       OR occurred_on IS NULL THEN
        RAISE EXCEPTION 'invalid commercial finance audit evidence' USING ERRCODE = '23514';
    END IF;
    IF current_setting('role', true) = 'nexora_tenant_app'
       AND business_unit_id IS DISTINCT FROM
           NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint THEN
        RAISE EXCEPTION 'commercial finance audit tenant mismatch' USING ERRCODE = '42501';
    END IF;

    IF aggregate_type = 'ReceivableDocument' THEN
        SELECT document."Status", document."DocumentType"
        INTO aggregate_status, aggregate_document_type
        FROM public."ReceivableDocuments" document
        WHERE document."BusinessUnitId" = business_unit_id AND document."Id" = aggregate_id;
        IF NOT FOUND OR audit_action NOT IN
            ('DraftCreated', 'AdjustmentDraftCreated', 'Issued', 'DraftCancelled')
           OR (audit_action = 'DraftCreated' AND
               (aggregate_status <> 'Draft' OR aggregate_document_type <> 'Invoice'))
           OR (audit_action = 'AdjustmentDraftCreated' AND
               (aggregate_status <> 'Draft' OR aggregate_document_type NOT IN ('CreditNote', 'DebitNote')))
           OR (audit_action IN ('Issued', 'DraftCancelled') AND aggregate_status <> 'Draft') THEN
            RAISE EXCEPTION 'audit action is inconsistent with the receivable document' USING ERRCODE = '23514';
        END IF;
    ELSIF aggregate_type = 'CustomerPayment' THEN
        SELECT payment."Status" INTO aggregate_status
        FROM public."CustomerPayments" payment
        WHERE payment."BusinessUnitId" = business_unit_id AND payment."Id" = aggregate_id;
        IF NOT FOUND OR audit_action NOT IN ('Posted', 'Reversed')
           OR (audit_action = 'Posted' AND aggregate_status <> 'Posted')
           OR (audit_action = 'Reversed' AND aggregate_status <> 'Reversed') THEN
            RAISE EXCEPTION 'audit action is inconsistent with the customer payment' USING ERRCODE = '23514';
        END IF;
    ELSIF aggregate_type = 'ReceivableWriteOff' THEN
        SELECT write_off."Status" INTO aggregate_status
        FROM public."ReceivableWriteOffs" write_off
        WHERE write_off."BusinessUnitId" = business_unit_id AND write_off."Id" = aggregate_id;
        IF NOT FOUND OR audit_action NOT IN ('DraftCreated', 'Posted', 'Cancelled', 'Reversed')
           OR aggregate_status <> (CASE audit_action
                WHEN 'DraftCreated' THEN 'Draft' ELSE audit_action END) THEN
            RAISE EXCEPTION 'audit action is inconsistent with the receivable write-off' USING ERRCODE = '23514';
        END IF;
    ELSIF aggregate_type = 'CustomerRefund' THEN
        SELECT refund."Status" INTO aggregate_status
        FROM public."CustomerRefunds" refund
        WHERE refund."BusinessUnitId" = business_unit_id AND refund."Id" = aggregate_id;
        IF NOT FOUND OR audit_action NOT IN ('DraftCreated', 'Approved', 'Released', 'Cancelled', 'Reversed',
                                             'DisbursementConfirmed', 'DisbursementFailed')
           OR aggregate_status <> (CASE audit_action
                WHEN 'DraftCreated' THEN 'Draft'
                WHEN 'DisbursementConfirmed' THEN 'Released'
                WHEN 'DisbursementFailed' THEN 'Released'
                ELSE audit_action END) THEN
            RAISE EXCEPTION 'audit action is inconsistent with the customer refund' USING ERRCODE = '23514';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported commercial finance audit aggregate' USING ERRCODE = '23514';
    END IF;

    INSERT INTO public."CommercialFinanceAudits"
        ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
    VALUES (business_unit_id, aggregate_type, aggregate_id, audit_action,
        audit_actor, occurred_on, audit_detail);
END
$$;


--
-- Name: nexora_write_finance_outbox(bigint, text, bigint, bigint, text, jsonb, timestamp without time zone); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_write_finance_outbox(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, event_type text, event_payload jsonb, event_time timestamp without time zone) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE deterministic_event_id uuid;
BEGIN
    deterministic_event_id := md5(concat_ws(':', business_unit_id, aggregate_type,
        aggregate_id, aggregate_version, event_type))::uuid;
    INSERT INTO public."FinanceOutboxMessages"
        ("BusinessUnitId", "EventId", "AggregateType", "AggregateId", "AggregateVersion",
         "EventType", "Payload", "SchemaVersion", "OccurredOn", "AvailableOn", "AttemptCount")
    VALUES (business_unit_id, deterministic_event_id, aggregate_type, aggregate_id,
        aggregate_version, event_type, event_payload, 1, event_time, event_time, 0)
    ON CONFLICT ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "EventType")
    DO NOTHING;
END
$$;


--
-- Name: nexora_write_off_allocation_governed(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_write_off_allocation_governed() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE write_off_row record;
DECLARE document_row record;
DECLARE live_outstanding numeric(18,2);
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'write-off allocations are immutable' USING ERRCODE = '55000';
    END IF;
    SELECT write_off.* INTO write_off_row
    FROM public."ReceivableWriteOffs" write_off
    WHERE write_off."BusinessUnitId" = NEW."BusinessUnitId"
      AND write_off."Id" = NEW."ReceivableWriteOffId" FOR UPDATE;
    IF NOT FOUND OR write_off_row."Status" <> 'Draft' THEN
        RAISE EXCEPTION 'allocations may only be appended to a new draft write-off' USING ERRCODE = '55000';
    END IF;
    SELECT document.* INTO document_row
    FROM public."ReceivableDocuments" document
    WHERE document."BusinessUnitId" = NEW."BusinessUnitId"
      AND document."Id" = NEW."ReceivableDocumentId" FOR UPDATE;
    IF NOT FOUND OR document_row."Status" <> 'Issued'
       OR document_row."DocumentType" NOT IN ('Invoice', 'DebitNote')
       OR (document_row."CustomerId", document_row."CurrencyId", document_row."CommercialCaseId")
          IS DISTINCT FROM
          (write_off_row."CustomerId", write_off_row."CurrencyId", write_off_row."CommercialCaseId") THEN
        RAISE EXCEPTION 'write-off allocation source identity is invalid' USING ERRCODE = '23514';
    END IF;
    live_outstanding := public.nexora_receivable_live_outstanding(
        NEW."BusinessUnitId", NEW."ReceivableDocumentId");
    IF NEW."Amount" <= 0 OR NEW."Amount" > live_outstanding
       OR NEW."BalanceBefore" <> live_outstanding
       OR NEW."BalanceAfter" <> round(live_outstanding - NEW."Amount", 2) THEN
        RAISE EXCEPTION 'write-off allocation exceeds or misstates the live document balance' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;


--
-- Name: nexora_write_off_governed(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_write_off_governed() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE legal_sequence bigint;
DECLARE fiscal_year integer;
DECLARE allocation_row record;
DECLARE allocation_total numeric(18,2);
DECLARE live_outstanding numeric(18,2);
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."WriteOffNumber" IS NOT NULL OR NEW."Version" <> 1
           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
           OR NEW."PostingStatus" <> 'NotPosted' OR NEW."JournalReference" IS NOT NULL
           OR length(trim(NEW."CreatedBy")) = 0 OR length(trim(NEW."ReasonCode")) = 0
           OR length(trim(NEW."Reason")) = 0 THEN
            RAISE EXCEPTION 'write-offs must be created as clean version-one drafts' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'write-offs cannot be deleted' USING ERRCODE = '55000';
    END IF;
    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Posted' THEN
        IF NEW."ApprovedBy" IS NULL OR length(trim(NEW."ApprovedBy")) = 0
           OR lower(trim(NEW."ApprovedBy")) = lower(trim(OLD."CreatedBy"))
           OR NEW."ApprovedOn" IS NULL OR NEW."Version" <> OLD."Version" + 1
           OR NEW."PostingStatus" <> 'PendingExport' OR NEW."JournalReference" IS NOT NULL
           OR NEW."WriteOffNumber" IS NULL
           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
           OR (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
               NEW."AccountingDate", NEW."TotalAmount", NEW."ReasonCode", NEW."Reason",
               NEW."EvidenceReference", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
               OLD."AccountingDate", OLD."TotalAmount", OLD."ReasonCode", OLD."Reason",
               OLD."EvidenceReference", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
            RAISE EXCEPTION 'invalid governed write-off posting transition' USING ERRCODE = '55000';
        END IF;
        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocation_total
        FROM public."WriteOffAllocations" allocation
        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
          AND allocation."ReceivableWriteOffId" = NEW."Id";
        IF allocation_total <> NEW."TotalAmount" OR allocation_total <= 0 THEN
            RAISE EXCEPTION 'write-off allocations do not reconcile to the header' USING ERRCODE = '23514';
        END IF;
        FOR allocation_row IN
            SELECT allocation.* FROM public."WriteOffAllocations" allocation
            WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
              AND allocation."ReceivableWriteOffId" = NEW."Id"
            ORDER BY allocation."ReceivableDocumentId"
        LOOP
            live_outstanding := public.nexora_receivable_live_outstanding(
                allocation_row."BusinessUnitId", allocation_row."ReceivableDocumentId");
            IF allocation_row."Amount" > live_outstanding
               OR allocation_row."BalanceBefore" <> live_outstanding
               OR allocation_row."BalanceAfter" <> round(live_outstanding - allocation_row."Amount", 2) THEN
                RAISE EXCEPTION 'write-off posting exceeds or misstates a live document balance' USING ERRCODE = '23514';
            END IF;
        END LOOP;
        fiscal_year := extract(year from NEW."AccountingDate")::integer;
        INSERT INTO public."LegalDocumentCounters"
            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
        VALUES (NEW."BusinessUnitId", 'WriteOff', fiscal_year, 2)
        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
        RETURNING "NextNumber" - 1 INTO legal_sequence;
        NEW."WriteOffNumber" := format('WOF-%s-%s', fiscal_year, lpad(legal_sequence::text, 6, '0'));
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
        IF NEW."CancelledBy" IS NULL OR length(trim(NEW."CancelledBy")) = 0
           OR NEW."CancelledOn" IS NULL OR NEW."CancellationReason" IS NULL
           OR length(trim(NEW."CancellationReason")) = 0 OR NEW."Version" <> OLD."Version" + 1
           OR NEW."WriteOffNumber" IS NOT NULL OR NEW."PostingStatus" <> OLD."PostingStatus"
           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
           OR (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
               NEW."AccountingDate", NEW."TotalAmount", NEW."ReasonCode", NEW."Reason", NEW."EvidenceReference",
               NEW."JournalReference", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
               OLD."AccountingDate", OLD."TotalAmount", OLD."ReasonCode", OLD."Reason", OLD."EvidenceReference",
               OLD."JournalReference", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
            RAISE EXCEPTION 'invalid governed write-off cancellation transition' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
        IF NEW."ReversedBy" IS NULL OR length(trim(NEW."ReversedBy")) = 0
           OR lower(trim(NEW."ReversedBy")) IN (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")))
           OR NEW."ReversedOn" IS NULL OR NEW."ReversalReason" IS NULL
           OR length(trim(NEW."ReversalReason")) = 0
           OR NEW."ReversalEvidenceReference" IS NULL OR length(trim(NEW."ReversalEvidenceReference")) = 0
           OR NEW."Version" <> OLD."Version" + 1 OR NEW."PostingStatus" <> 'ReversalPendingExport'
           OR (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
               NEW."WriteOffNumber", NEW."AccountingDate", NEW."TotalAmount", NEW."ReasonCode", NEW."Reason",
               NEW."EvidenceReference", NEW."JournalReference", NEW."IdempotencyKey", NEW."RequestHash",
               NEW."CreatedBy", NEW."CreatedOn", NEW."ApprovedBy", NEW."ApprovedOn",
               NEW."CancelledBy", NEW."CancelledOn", NEW."CancellationReason")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
               OLD."WriteOffNumber", OLD."AccountingDate", OLD."TotalAmount", OLD."ReasonCode", OLD."Reason",
               OLD."EvidenceReference", OLD."JournalReference", OLD."IdempotencyKey", OLD."RequestHash",
               OLD."CreatedBy", OLD."CreatedOn", OLD."ApprovedBy", OLD."ApprovedOn",
               OLD."CancelledBy", OLD."CancelledOn", OLD."CancellationReason") THEN
            RAISE EXCEPTION 'invalid governed write-off reversal transition' USING ERRCODE = '55000';
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'write-off fields and lifecycle are immutable outside governed transitions' USING ERRCODE = '55000';
END
$$;


--
-- Name: nexora_write_off_outbox_event(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_write_off_outbox_event() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE event_action text;
DECLARE event_time timestamp without time zone;
DECLARE event_actor text;
BEGIN
    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
        event_action := 'DraftCreated'; event_time := NEW."CreatedOn"; event_actor := NEW."CreatedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" = 'Posted' THEN
        event_action := 'Posted'; event_time := NEW."ApprovedOn"; event_actor := NEW."ApprovedBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
        event_action := 'Cancelled'; event_time := NEW."CancelledOn"; event_actor := NEW."CancelledBy";
    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
        event_action := 'Reversed'; event_time := NEW."ReversedOn"; event_actor := NEW."ReversedBy";
    ELSE RETURN NEW;
    END IF;
    PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableWriteOff',
        NEW."Id", event_action, event_actor, jsonb_build_object(
            'number', NEW."WriteOffNumber", 'amount', NEW."TotalAmount",
            'reasonCode', NEW."ReasonCode", 'version', NEW."Version"), event_time);
    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'ReceivableWriteOff',
        NEW."Id", NEW."Version", 'finance.write-off.' || CASE event_action
            WHEN 'DraftCreated' THEN 'draft-created' ELSE lower(event_action) END,
        jsonb_build_object('Id', NEW."Id", 'Status', NEW."Status",
            'WriteOffNumber', NEW."WriteOffNumber", 'CustomerId', NEW."CustomerId",
            'CommercialCaseId', NEW."CommercialCaseId", 'CurrencyId', NEW."CurrencyId",
            'TotalAmount', NEW."TotalAmount", 'ReasonCode', NEW."ReasonCode",
            'Actor', event_actor, 'Version', NEW."Version"), event_time);
    RETURN NEW;
END
$$;


--
-- Name: nexora_write_otc_audit(bigint, text, bigint, bigint, text, text, text, text, text, text, text, jsonb, text, timestamp without time zone); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.nexora_write_otc_audit(business_unit_id bigint, aggregate_type text, aggregate_id bigint, aggregate_version bigint, command_type text, previous_state text, new_state text, actor text, reason text, request_hash text, idempotency_key text, result_json jsonb, correlation_id text, occurred_on timestamp without time zone) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $_$
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
    AND previous_state = new_state AND new_state <> 'CANCELLED')) THEN
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
$_$;


--
-- Name: wave1_reject_append_only_mutation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE OR REPLACE FUNCTION public.wave1_reject_append_only_mutation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Wave 1 governance events are append-only';
END $$;


SET LOCAL default_tablespace = '';

SET LOCAL default_table_access_method = heap;
