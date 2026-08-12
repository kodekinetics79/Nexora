using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <summary>
/// What one probe run saw, as an artifact somebody can recompute.
/// </summary>
/// <param name="Satisfied">
/// False means the boundary was NOT observed to be what the manifest says. The caller must fail;
/// there is no partial credit and no "probably".
/// </param>
/// <param name="Failure">One sentence naming what disagreed, or null when nothing did.</param>
/// <param name="CanonicalJson">
/// The observation itself. This is the document <see cref="EvidenceSha256"/> is the hash OF, and
/// it is written verbatim into the probe's audit record so the hash can be recomputed from the
/// audit trail alone.
/// </param>
/// <param name="EvidenceReference">
/// Content-addressed: it carries the hash, so a reference and a document that do not match cannot
/// be mistaken for each other. Opaque by the registry's rules — no scheme separator, no query, no
/// credential.
/// </param>
public sealed record TenantScopeProbeResult(
    bool Satisfied,
    string? Failure,
    string CanonicalJson,
    string EvidenceSha256,
    string EvidenceReference,
    long? ObservedBusinessUnitId,
    string ObservedRegion);

public interface ITenantPostgreSqlScopeProbe
{
    /// <summary>
    /// Observes the tenant's primary PostgreSQL scope through the supplied context and returns the
    /// evidence document plus its hash. Never throws for a disagreement — a disagreement is a
    /// result, and the caller needs the document that records it.
    /// </summary>
    Task<TenantScopeProbeResult> ObserveAsync(
        ErpRfqAutomationContext db, Tenant tenant, PlatformDataBoundary boundary, CancellationToken ct);
}

/// <summary>
/// The real observation behind an automatic <c>data.residency-isolation</c> pass.
///
/// <para><b>Why this exists rather than a constant.</b> The activation control requires a
/// verification evidence reference and a SHA-256, and the temptation when automating the manual
/// form is to supply a fixed string for both. That would be the worst possible outcome: the
/// control would read as verified on every tenant forever, including the tenant whose business
/// unit is missing and the deployment whose row-level security was never enabled. A false pass on
/// a data-residency control is strictly worse than the manual step it replaces, because a manual
/// step at least has a person behind it.</para>
///
/// <para><b>What is actually observed.</b> Four things, all read from the running system at the
/// moment the step executes:</para>
/// <list type="number">
/// <item>the tenant's <c>PrimaryBusinessUnitId</c>, read from <c>platform."Tenants"</c>;</item>
/// <item>that the business unit it names EXISTS and is active, read from <c>BusinessUnits</c>;</item>
/// <item>that tenant isolation is genuinely in force for the tables that carry a business unit —
/// on PostgreSQL, <c>relrowsecurity</c> plus a <c>nexora_tenant_isolation</c> policy bound to
/// <c>nexora_tenant_app</c> and keyed on <c>nexora.business_unit_id</c>, which is the same
/// property <c>PostgreSqlProductionDialectTests</c> enforces; on every other provider, the EF
/// global query filter that is the only isolation layer those providers have, named as such in
/// the evidence so nobody can read a SQLite observation as a PostgreSQL one;</item>
/// <item>the region this deployment declares for the boundary, and whether it agrees with the
/// tenant's contractual <c>DataRegion</c>.</item>
/// </list>
///
/// <para><b>The region is a configured fact, and says so.</b> Nothing here geolocates a database.
/// The document records <c>regionSource</c> as the configuration key it came from, so an auditor
/// reading it knows exactly what claim is being made and by whom.</para>
/// </summary>
public sealed class TenantPostgreSqlScopeProbe : ITenantPostgreSqlScopeProbe
{
    /// <summary>Bumped when the shape of the observation changes, so two documents are never compared across shapes.</summary>
    public const string ProbeVersion = "nexora.tenant-postgresql-scope/2026-08-12.v1";

    /// <summary>
    /// The four entity types the non-PostgreSQL branch checks by name. A regression that dropped a
    /// query filter from one of these would take the isolation layer off a commercial document,
    /// which is the failure worth naming rather than counting.
    /// </summary>
    private static readonly (string Name, Type Clr)[] CoreTenantEntities =
    [
        ("Leads", typeof(Lead)),
        ("Quotes", typeof(Quote)),
        ("Orders", typeof(Order)),
        ("SetupMasters", typeof(SetupMaster))
    ];

    public async Task<TenantScopeProbeResult> ObserveAsync(
        ErpRfqAutomationContext db, Tenant tenant, PlatformDataBoundary boundary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(boundary);

        var failures = new List<string>();

        var businessUnitId = tenant.PrimaryBusinessUnitId;
        if (businessUnitId is null)
            failures.Add("The tenant has no primary business unit, so it has no PostgreSQL scope to verify.");

        var unit = businessUnitId is long id
            ? await db.Set<BusinessUnit>().IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.BusinessUnitCode, x.IsActive })
                .FirstOrDefaultAsync(ct)
            : null;
        if (businessUnitId is not null && unit is null)
            failures.Add($"Business unit {businessUnitId} is recorded on the tenant but does not exist.");
        else if (unit is { IsActive: false })
            failures.Add($"Business unit {unit.Id} exists but is inactive, so it is not a live tenant scope.");

        var contractualRegion = tenant.DataRegion?.Trim();
        if (string.IsNullOrWhiteSpace(contractualRegion))
            failures.Add("The tenant has no contractual data region recorded, so nothing can be agreed with.");
        else if (!string.Equals(contractualRegion, boundary.Region, StringComparison.OrdinalIgnoreCase))
            failures.Add(
                $"The manifest declares region '{boundary.Region}' for this boundary but the tenant's "
                + $"contractual data region is '{contractualRegion}'.");

        var isolation = await ObserveIsolationAsync(db, ct);
        if (!isolation.Enforced)
            failures.Add(isolation.Detail);

        // Not evidence of isolation on its own — one tenant in an empty database cannot fail a
        // visibility test — but it is what makes the document say something about THIS tenant
        // rather than about the schema in general.
        var scopedRows = businessUnitId is long scope
            ? await db.SetupMasters.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.BusinessUnitId == scope, ct)
            : 0;

        var document = new ProbeDocument(
            ProbeVersion,
            DateTime.UtcNow.ToString("O"),
            tenant.Id,
            tenant.Slug,
            boundary.LogicalKey,
            boundary.AssetType,
            boundary.OpaqueProviderReference,
            businessUnitId,
            unit?.BusinessUnitCode,
            unit?.IsActive,
            scopedRows,
            contractualRegion,
            boundary.Region,
            $"configuration:{PlatformDataBoundaryManifest.SectionName}:{boundary.AssetType}:Region",
            db.Database.ProviderName ?? "unknown",
            isolation.Mechanism,
            isolation.Enforced,
            isolation.TablesObserved,
            isolation.TablesDeficient,
            failures.Count == 0,
            failures.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // Deterministic by construction: System.Text.Json writes a record's properties in
        // declaration order, the two collections are ordered above, and there is no culture in
        // play. Two runs that saw the same thing produce the same bytes and therefore the same
        // hash — which is the only reason a content-addressed reference means anything.
        var canonical = JsonSerializer.Serialize(document, CanonicalJson);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        return new TenantScopeProbeResult(
            failures.Count == 0,
            failures.Count == 0 ? null : string.Join(" ", failures),
            canonical,
            sha256,
            $"urn:nexora:data-boundary-probe:{boundary.LogicalKey}:tenant-{tenant.Id}:sha256-{sha256}",
            businessUnitId,
            boundary.Region);
    }

    private static readonly JsonSerializerOptions CanonicalJson = new() { WriteIndented = false };

    private sealed record IsolationObservation(
        bool Enforced, string Mechanism, string Detail, int TablesObserved, IReadOnlyList<string> TablesDeficient);

    private static async Task<IsolationObservation> ObserveIsolationAsync(
        ErpRfqAutomationContext db, CancellationToken ct)
    {
        // Every public-schema entity that carries a global query filter — i.e. every table the
        // model itself says is tenant-scoped. Derived from the model rather than from a hand-kept
        // list so a table added tomorrow is probed tomorrow.
        var tables = db.Model.GetEntityTypes()
            .Where(entity => entity.GetQueryFilter() is not null && (entity.GetSchema() ?? "public") == "public")
            .Select(entity => entity.GetTableName())
            .Where(table => !string.IsNullOrWhiteSpace(table))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray()!;

        if (!db.Database.IsNpgsql())
        {
            // SQLite and any other provider have no roles and no row-level security. Saying
            // "RLS is in force" here would be a lie, so the document names the layer that IS in
            // force — the EF global query filter — and the provider it is running on. A reader
            // can always tell a PostgreSQL observation from this one.
            var missing = CoreTenantEntities
                .Where(entity => db.Model.FindEntityType(entity.Clr)?.GetQueryFilter() is null)
                .Select(entity => entity.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            return new IsolationObservation(
                missing.Length == 0,
                "ef-global-query-filter",
                missing.Length == 0
                    ? "Tenant isolation on this provider is the EF global query filter."
                    : "These tenant-scoped entities have no global query filter, so nothing isolates "
                      + "them on this provider: " + string.Join(", ", missing) + ".",
                tables.Length,
                missing);
        }

        var deficient = await DeficientRlsTablesAsync(db, tables, ct);
        return new IsolationObservation(
            deficient.Count == 0,
            "postgresql-row-level-security",
            deficient.Count == 0
                ? "Every tenant-scoped table enforces the nexora_tenant_isolation policy."
                : "These tenant-scoped tables do not enforce a nexora_tenant_isolation policy keyed on "
                  + "nexora.business_unit_id, so row-level security is not isolating this tenant: "
                  + string.Join(", ", deficient) + ".",
            tables.Length,
            deficient);
    }

    /// <summary>
    /// The same predicate <c>PostgreSqlProductionDialectTests</c> asserts, asked of the live
    /// database instead of a test fixture: security enabled, a <c>nexora_tenant_isolation</c>
    /// policy present with both a USING and a WITH CHECK expression, granted to
    /// <c>nexora_tenant_app</c>, and both expressions keyed on <c>nexora.business_unit_id</c>.
    /// Anything less and a tenant's rows are visible to another tenant's session.
    /// </summary>
    private static async Task<IReadOnlyList<string>> DeficientRlsTablesAsync(
        ErpRfqAutomationContext db, string[] tables, CancellationToken ct)
    {
        if (tables.Length == 0) return [];

        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                WITH expected(table_name) AS (SELECT unnest(@tables::text[]))
                SELECT string_agg(expected.table_name, ',' ORDER BY expected.table_name)
                FROM expected
                LEFT JOIN pg_class table_definition ON table_definition.relname = expected.table_name
                LEFT JOIN pg_namespace schema_definition
                    ON schema_definition.oid = table_definition.relnamespace
                   AND schema_definition.nspname = 'public'
                LEFT JOIN pg_policy policy
                    ON policy.polrelid = table_definition.oid
                   AND policy.polname = 'nexora_tenant_isolation'
                LEFT JOIN pg_roles tenant_role ON tenant_role.rolname = 'nexora_tenant_app'
                WHERE schema_definition.oid IS NULL
                   OR NOT table_definition.relrowsecurity
                   OR policy.oid IS NULL
                   OR policy.polqual IS NULL
                   OR policy.polwithcheck IS NULL
                   OR NOT tenant_role.oid = ANY(policy.polroles)
                   OR position('nexora.business_unit_id' in pg_get_expr(policy.polqual, policy.polrelid)) = 0
                   OR position('nexora.business_unit_id' in pg_get_expr(policy.polwithcheck, policy.polrelid)) = 0;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "tables";
            parameter.Value = tables;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(ct) as string;
            return string.IsNullOrWhiteSpace(result)
                ? []
                : result.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// The evidence artifact. Property ORDER is part of the contract — it is what makes the
    /// serialisation canonical — so fields are appended, never reordered, and any change to the
    /// shape bumps <see cref="ProbeVersion"/>.
    /// </summary>
    private sealed record ProbeDocument(
        string Probe,
        string ObservedAtUtc,
        long TenantId,
        string TenantSlug,
        string LogicalKey,
        string AssetType,
        string OpaqueProviderReference,
        long? ObservedBusinessUnitId,
        string? ObservedBusinessUnitCode,
        bool? ObservedBusinessUnitIsActive,
        int ObservedBusinessUnitConfigurationRows,
        string? ContractualDataRegion,
        string DeclaredRegion,
        string RegionSource,
        string DatabaseProvider,
        string IsolationMechanism,
        bool IsolationEnforced,
        int IsolationTablesObserved,
        IReadOnlyList<string> IsolationTablesDeficient,
        bool Satisfied,
        IReadOnlyList<string> Disagreements);
}
