using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>Why a slug was refused, so the console can style the message and a test can assert on it.</summary>
public enum SlugRefusalReason
{
    None,

    /// <summary>Nothing usable survived slugification — the name was punctuation or non-Latin script.</summary>
    Empty,

    /// <summary>Wrong shape: bad characters, leading/trailing hyphen, or outside the length window.</summary>
    Malformed,

    /// <summary>Would collide with a route, a static path or an infrastructure hostname.</summary>
    RouteCollision,

    /// <summary>Could be read as Nexora itself, or as a privileged part of the platform.</summary>
    Impersonation,

    /// <summary>Reads as an identifier rather than a name — all digits, or a punycode label.</summary>
    Confusable
}

/// <summary>The verdict on one proposed slug.</summary>
/// <param name="Slug">The normalised slug, when it was accepted.</param>
/// <param name="Reason">Why it was refused. <see cref="SlugRefusalReason.None"/> on success.</param>
/// <param name="Message">Operator-facing refusal text. Null on success.</param>
public sealed record SlugVerdict(string? Slug, SlugRefusalReason Reason, string? Message)
{
    public bool IsAccepted => Reason == SlugRefusalReason.None;

    public static SlugVerdict Accepted(string slug) => new(slug, SlugRefusalReason.None, null);

    public static SlugVerdict Refused(SlugRefusalReason reason, string message)
        => new(null, reason, message);
}

/// <summary>
/// The names a tenant may not have.
///
/// <para><b>Why this matters more than it looks, given nothing routes on the slug today.</b> Two
/// reasons, and the second is the sharper one.</para>
///
/// <para><b>1. The slug is permanent on the customer's documents.</b>
/// <c>TenantsController.Provision</c> derives <c>BusinessUnit.BusinessUnitCode</c> from it by
/// uppercasing, <c>TenantBaselineSeeder</c> derives <c>LeadReferenceConfiguration.Prefix</c> from
/// that, and <c>LeadPersistenceRules</c> stamps the result into every commercial case's
/// permanent <c>MasterReference</c> — printed on quotes, immutable once allocated. A tenant
/// provisioned as <c>admin</c> issues quotes referenced <c>ADMIN-2026-000001</c> forever. There
/// is no rename path that fixes documents already sent to a customer.</para>
///
/// <para><b>2. The product already tells operators the slug is an address.</b> The provisioning
/// wizard's own helper text says "Used in the workspace address and the tenant id", and the field
/// is described in <c>Tenant.cs</c> as a "URL/subdomain-safe identifier". Nothing consumes it as
/// an address yet. The moment something does — a <c>/{slug}</c> route, a
/// <c>{slug}.nexora.app</c> host — every existing tenant's slug becomes load-bearing
/// retroactively, and a tenant already holding <c>api</c>, <c>login</c> or <c>platform</c> cannot
/// be renamed out of the way without breaking their references. Reserving costs nothing today
/// and is impossible later.</para>
///
/// <para><b>What the list is drawn from</b>, rather than invented: the backend's own
/// <c>[Route]</c> prefixes and top-level endpoints (<c>/health</c>, <c>/ready</c>,
/// <c>/metrics</c>, <c>/swagger</c>), the console's top-level React routes (which are served by a
/// catch-all rewrite, so any one of them shadows a future <c>/{slug}</c>), the RFC 2142 role
/// mailbox names, the conventional infrastructure hostnames, and the vendor's own identity.</para>
/// </summary>
public static class ReservedTenantSlugs
{
    /// <summary>
    /// Upper bound, and it is NOT cosmetic. <c>BusinessUnits.BusinessUnitCode</c> is
    /// <c>varchar(50)</c> while the existing <c>Slugify</c> truncates at 60, so a 51–60 character
    /// slug passes the tenant insert and then fails the business-unit insert — inside the same
    /// transaction, surfacing as the generic "Provisioning failed." with no field named. Capped
    /// at the width the derived column can actually hold.
    /// </summary>
    public const int MaximumLength = 50;

    /// <summary>
    /// Lower bound. Two characters is the floor the console already enforces; one-character
    /// slugs are also the scarcest namespace there is, and handing one to whoever provisions
    /// first is not a decision provisioning should be making silently.
    /// </summary>
    public const int MinimumLength = 3;

    /// <summary>
    /// Shape rule, applied to the slug AFTER derivation. The console enforces the same pattern;
    /// this is the copy that binds, because a rule that lives only in the client is a rule any
    /// other caller ignores — and the current server simply re-slugifies whatever arrives, so
    /// today a direct API call can create a tenant slugged <c>404</c>.
    /// </summary>
    private static readonly Regex Shape = new(
        "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>All digits. Reserved because an identifier that is indistinguishable from a
    /// numeric id is ambiguous the first time anything resolves <c>/{idOrSlug}</c>.</summary>
    private static readonly Regex AllDigits = new(
        "^[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Anything that begins with the vendor's own name. A blocklist of exact strings cannot
    /// cover <c>nexora-support</c>, <c>nexorabilling</c> or <c>nexora-security</c>, and those are
    /// precisely the names a phishing setup would ask for.
    /// </summary>
    private static readonly string[] ImpersonationPrefixes = ["nexora", "kodekinetics"];

    /// <summary>
    /// Backend route prefixes, top-level endpoints, console routes and static asset paths — the
    /// names that would shadow, or be shadowed by, an existing URL. Sourced from the actual
    /// route table rather than guessed.
    /// </summary>
    private static readonly string[] RouteNames =
    [
        // Backend prefixes and top-level endpoints.
        "api", "apis", "graphql", "health", "healthz", "ready", "readyz", "live", "metrics",
        "swagger", "openapi", "hangfire", "signalr", "hubs", "ws", "socket",
        // Served or reserved asset paths.
        "static", "assets", "public", "wwwroot", "uploads", "files", "media", "images", "img",
        "css", "js", "fonts", "favicon", "robots", "sitemap",
        // Console top-level routes (a catch-all rewrite serves these, so any one of them wins
        // against a future /{slug}).
        "activate", "admin", "analytics", "commercial-cases", "copilot", "customers", "dashboard",
        "executive", "intelligence", "inventory", "leads", "login", "orders", "platform",
        "procurement", "quotations", "rfqs", "sales", "security", "services", "setup", "sourcing",
        "suppliers", "settings", "profile", "notifications", "reports",
        // Authentication and account paths a tenant address must never sit on top of.
        "logout", "signin", "signout", "signup", "register", "auth", "oauth", "oauth2", "sso",
        "saml", "oidc", "token", "tokens", "session", "sessions", "account", "accounts",
        "password", "passwords", "reset", "activation", "invite", "invitation", "invitations",
        "verify", "confirm", "callback", "connect",
        // Conventional hostnames. A slug that becomes a subdomain must not be one of these.
        "www", "www2", "web", "app", "apps", "mail", "email", "smtp", "imap", "pop", "pop3",
        "ftp", "sftp", "ssh", "ns", "ns1", "ns2", "ns3", "mx", "mx1", "dns", "cdn", "edge",
        "origin", "proxy", "gateway", "vpn", "lb", "router", "host", "server",
        // Environment names. A tenant called "staging" makes every operational sentence ambiguous.
        "dev", "develop", "development", "test", "tests", "testing", "stage", "staging", "qa",
        "uat", "prod", "production", "sandbox", "demo", "preview", "beta", "alpha", "canary",
        "local", "localhost", "internal", "intranet",
        // Data plane hostnames.
        "db", "database", "sql", "postgres", "postgresql", "redis", "cache", "queue", "broker",
        "s3", "blob", "storage", "backup", "backups", "archive",
        // Well-known and reserved by convention.
        "well-known", "acme-challenge", "null", "undefined", "none", "true", "false", "nan",
        "new", "edit", "create", "delete", "update", "index", "default", "example", "sample",
        "about", "legal", "privacy", "terms", "pricing", "blog", "news", "help", "contact",
        "careers", "jobs", "search", "status", "config", "configuration"
    ];

    /// <summary>
    /// Names that assert authority, represent the vendor, or are RFC 2142 role mailboxes. A
    /// customer holding one of these is not merely confusing — <c>acme</c> emailing as
    /// <c>support@</c> and a tenant literally named <c>support</c> are different problems, and
    /// the second one is ours to prevent.
    /// </summary>
    private static readonly string[] ImpersonationNames =
    [
        // Privilege.
        "admin", "admins", "administrator", "administrators", "root", "superuser", "superadmin",
        "super-admin", "sudo", "system", "sys", "operator", "operators", "owner", "owners",
        "staff", "team", "official", "moderator", "moderators",
        // Vendor and platform identity.
        "nexora", "platform", "console", "control", "controlplane", "control-plane", "core",
        "service", "services-account", "bot", "bots", "daemon", "worker", "workers",
        // RFC 2142 role addresses and their relatives. These reach real inboxes at most
        // organisations, so a tenant address that matches one is a mail-routing hazard on top of
        // being an impersonation one.
        "support", "helpdesk", "help-desk", "abuse", "postmaster", "hostmaster", "webmaster",
        "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "security",
        "info", "sales", "marketing", "press", "legal-notices", "compliance", "privacy-office",
        // Money. A tenant named "billing" in a system that bills tenants is a support incident
        // waiting to happen, in both directions.
        "billing", "billings", "payment", "payments", "invoice", "invoices", "finance",
        "accounting", "subscription", "subscriptions", "checkout", "refund", "refunds"
    ];

    private static readonly HashSet<string> Reserved =
        new(RouteNames.Concat(ImpersonationNames), StringComparer.Ordinal);

    /// <summary>
    /// Slugifies and then judges. This is the ONLY entry point callers should use: deriving a
    /// slug and checking it separately is how a name like "Admin Ltd." gets checked as
    /// "Admin Ltd." (not reserved) and stored as "admin-ltd" (fine) while "Admin" gets checked as
    /// "Admin" (not reserved, wrong case) and stored as "admin" (very much not fine).
    /// </summary>
    /// <param name="requestedSlug">What the operator typed in the slug field, if anything.</param>
    /// <param name="tenantName">Fallback source when no slug was supplied.</param>
    public static SlugVerdict Evaluate(string? requestedSlug, string? tenantName)
    {
        var typed = !string.IsNullOrWhiteSpace(requestedSlug);
        var source = typed ? requestedSlug : tenantName;
        var slug = Slugify(source);

        if (string.IsNullOrEmpty(slug))
            return SlugVerdict.Refused(SlugRefusalReason.Empty,
                "A workspace address could not be derived from this name — it contains no letters " +
                "or digits that survive normalisation. Type one explicitly in the slug field.");

        // An address the operator TYPED is never silently rewritten; one DERIVED from the company
        // name is truncated without comment.
        //
        // The asymmetry is the whole point. This slug is not cosmetic and it is not editable later:
        // it becomes the BusinessUnitCode, which becomes the LeadReferenceConfiguration prefix,
        // which is stamped into every commercial case's immutable MasterReference and printed on
        // the customer's quotes forever. Quietly handing back a different address than the one that
        // was typed means the operator agreed a workspace address with the customer and the system
        // issued a different one — discovered, at the earliest, on the first quote. Someone who
        // never typed an address has expressed no expectation to violate, so shortening a long
        // company name is unremarkable.
        if (typed && Slugify(requestedSlug!.Trim()).Length < NormalizeWithoutCap(requestedSlug).Length)
            return SlugVerdict.Refused(SlugRefusalReason.Malformed,
                $"The workspace address is limited to {MaximumLength} characters because it becomes " +
                "this tenant's business unit code, and that address is permanent — it is printed on " +
                "every quote this customer issues. Shorten it rather than have one chosen for you.");

        return Judge(slug);
    }

    /// <summary>
    /// Judges an ALREADY normalised slug. Split out so the wizard's live "is this available?"
    /// check and the submit path share one rule set rather than drifting apart.
    /// </summary>
    public static SlugVerdict Judge(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);

        if (slug.Length < MinimumLength)
            return SlugVerdict.Refused(SlugRefusalReason.Malformed,
                $"'{slug}' is too short — a workspace address needs at least {MinimumLength} " +
                "characters. Very short addresses are a scarce shared namespace and are not " +
                "allocated on a first-come basis.");

        if (slug.Length > MaximumLength)
            return SlugVerdict.Refused(SlugRefusalReason.Malformed,
                $"'{slug}' is {slug.Length} characters; the limit is {MaximumLength}. The address " +
                "becomes this tenant's business unit code, which is a 50-character column, and a " +
                "longer value fails at the point of creation rather than here.");

        if (!Shape.IsMatch(slug))
            return SlugVerdict.Refused(SlugRefusalReason.Malformed,
                $"'{slug}' is not a valid workspace address. Use lowercase letters, digits and " +
                "single hyphens between them; it cannot start or end with a hyphen.");

        if (AllDigits.IsMatch(slug))
            return SlugVerdict.Refused(SlugRefusalReason.Confusable,
                $"'{slug}' is all digits, which is indistinguishable from a tenant id. Include at " +
                "least one letter so the address can never be mistaken for a record number.");

        // RFC 5891 reserves every label whose third and fourth characters are hyphens; "xn--"
        // in particular is the punycode prefix, and a slug that decodes to non-Latin script is a
        // homograph attack the moment the address appears in a hostname or a link.
        if (slug.Length > 3 && slug[2] == '-' && slug[3] == '-')
            return SlugVerdict.Refused(SlugRefusalReason.Confusable,
                $"'{slug}' uses the reserved two-character-then-double-hyphen form (RFC 5891). " +
                "Addresses in that shape are decoded as internationalised names and can be made " +
                "to display as something else entirely.");

        foreach (var prefix in ImpersonationPrefixes)
        {
            if (!slug.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            return SlugVerdict.Refused(SlugRefusalReason.Impersonation,
                $"'{slug}' begins with '{prefix}', which is the platform vendor's own name. " +
                "Addresses that could be read as the vendor speaking are refused regardless of " +
                "who is asking for them, because the customer receiving the document cannot tell " +
                "the difference. Choose a different one.");
        }

        if (!Reserved.Contains(slug))
            return SlugVerdict.Accepted(slug);

        var isRoute = RouteNames.Contains(slug, StringComparer.Ordinal);
        return SlugVerdict.Refused(
            isRoute ? SlugRefusalReason.RouteCollision : SlugRefusalReason.Impersonation,
            isRoute
                ? $"'{slug}' is a reserved address: it is already a route, an asset path or a " +
                  "standard hostname in this product, so a tenant holding it would shadow or be " +
                  "shadowed by an existing URL. Choose a different one."
                : $"'{slug}' is a reserved address: it names a privileged role, the platform " +
                  "vendor, or a standard organisational mailbox, and a tenant holding it could be " +
                  "mistaken for the platform itself. Choose a different one.");
    }

    /// <summary>
    /// Normalisation, matching <c>TenantsController.Slugify</c> in behaviour except for the
    /// length cap — 50 rather than 60, for the business-unit-code reason documented on
    /// <see cref="MaximumLength"/>. Kept here rather than shared, because the controller's copy
    /// is the compatibility surface and must not change underneath its existing tests.
    /// </summary>
    public static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var slug = NormalizeWithoutCap(input);
        return slug.Length > MaximumLength ? slug[..MaximumLength].Trim('-') : slug;
    }

    /// <summary>
    /// The same normalisation with the length cap NOT applied, so a caller can tell the difference
    /// between an address that fits and one that only fits after being shortened.
    /// </summary>
    private static string NormalizeWithoutCap(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var lowered = input.Trim().ToLowerInvariant();
        return Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
    }

    /// <summary>Every reserved word, for the console's "why was this refused?" affordance and
    /// for the test that pins the list against the live route table.</summary>
    public static IReadOnlyCollection<string> All => Reserved;
}
