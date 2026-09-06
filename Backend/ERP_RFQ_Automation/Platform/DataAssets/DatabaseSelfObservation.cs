using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <summary>
/// What the running process can say about the database it is connected to, right now.
/// </summary>
/// <param name="Host">The host it is actually connected to. Never a credential — the password is
/// not in the connection's public description.</param>
/// <param name="ProviderName">A human name for the hosting provider: "Neon", "Amazon RDS", …</param>
/// <param name="OpaqueProviderReference">
/// A stable, opaque identifier for this database, derived from the host — the Neon endpoint id, the
/// RDS instance id. Null when the host shape is not one this code recognises, because a made-up
/// identifier is worse than none.
/// </param>
/// <param name="Region">The hosting region read out of the host name, or null.</param>
/// <param name="Basis">
/// One sentence naming what was read and from where, for the console and for the audit record. An
/// observation always carries its own provenance: "us-east-1" alone is a claim, "us-east-1, read
/// from the host this process is connected to" is evidence.
/// </param>
public sealed record DatabaseSelfObservation(
    string? Host,
    string? ProviderName,
    string? OpaqueProviderReference,
    string? Region,
    string Basis)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(OpaqueProviderReference)
                            && !string.IsNullOrWhiteSpace(Region);
}

public interface IDatabaseSelfObserver
{
    /// <summary>Reads the live connection. Never throws — an unrecognised host is a null field.</summary>
    DatabaseSelfObservation Observe(ErpRfqAutomationContext db);
}

/// <summary>
/// The platform reading its own address, so nobody has to type it.
///
/// <para><b>Why this exists.</b> <c>data.residency-isolation</c> needs a provider reference and a
/// region for the database every tenant lives in. Both were operator input: four opaque fields per
/// tenant in a dialog, or four environment variables on the API service. Neither is answerable by
/// the person who actually hits this — an operator onboarding a customer does not know the Neon
/// endpoint id, and telling them to go and set an environment variable is the same demand wearing
/// a different hat. Meanwhile the process is, at that exact moment, holding an open connection to
/// the database in question.</para>
///
/// <para><b>Why this is not "inventing" the answer the manifest refuses to invent.</b>
/// <see cref="PlatformDataBoundaryManifest"/> declines to default a region because a guessed region
/// is a residency claim nobody made. This makes no guess: it reports the host the process is
/// connected to and what that host says about itself, and every value it returns carries
/// <see cref="DatabaseSelfObservation.Basis"/> naming where it was read. It is also strictly better
/// evidence than the alternative it replaces — a string an operator typed from memory can be wrong
/// about the running system; the running system's own connection cannot.</para>
///
/// <para><b>It still does not decide anything.</b> Nothing here is registered against a tenant.
/// The observation is a SUGGESTION the console shows an Owner, who confirms it, and the
/// confirmation is what gets stored and audited. The machine proposes; a person disposes; the audit
/// says which of the two did what.</para>
/// </summary>
public sealed class DatabaseSelfObserver : IDatabaseSelfObserver
{
    /// <summary>
    /// Host shapes this code can read, most specific first.
    ///
    /// <para>Deliberately a small closed list. A regex general enough to pull a "region" out of any
    /// hostname would eventually pull one out of a host that has no region, and would state it with
    /// the same confidence as a real one.</para>
    /// </summary>
    public DatabaseSelfObservation Observe(ErpRfqAutomationContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        string? host;
        try
        {
            // The connection's own description, not configuration: on a deployment whose
            // connection string was changed in the dashboard and never in the repository, these
            // disagree, and the one that matters is the one carrying the queries.
            host = db.Database.GetDbConnection().DataSource?.Trim();
        }
        catch (Exception)
        {
            // A provider that cannot describe its connection is not an error worth failing a
            // screen over. It reads as "nothing observed", and the manual path is still there.
            return new DatabaseSelfObservation(null, null, null, null,
                "The database connection could not be described by its provider.");
        }

        if (string.IsNullOrWhiteSpace(host))
            return new DatabaseSelfObservation(null, null, null, null,
                "This process reports no database host, so nothing could be read from it.");

        var bare = HostOf(host);
        if (bare is null)
            return new DatabaseSelfObservation(host, null, null, null,
                $"The database connection reports '{host}', which does not contain a host name this "
                + "deployment can read, so the database has to be named by hand.");

        var labels = bare.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // Neon: <endpoint-id>[-pooler].<something>.<region>.<cloud>.neon.tech
        // The endpoint id is the tenant-independent name of the compute, and the region is a label
        // two places before the cloud vendor.
        if (bare.EndsWith(".neon.tech", StringComparison.Ordinal) && labels.Length >= 4)
        {
            var endpoint = labels[0].EndsWith("-pooler", StringComparison.Ordinal)
                ? labels[0][..^"-pooler".Length]
                : labels[0];
            // …aws.neon.tech / …azure.neon.tech — the label before the cloud is the region.
            var cloudIndex = Array.FindLastIndex(labels, l => l is "aws" or "azure" or "gcp");
            var region = cloudIndex > 0 ? labels[cloudIndex - 1] : null;
            return new DatabaseSelfObservation(bare, "Neon",
                string.IsNullOrWhiteSpace(endpoint) ? null : $"neon-{endpoint}", region,
                $"Read from the database host this process is connected to ({bare}).");
        }

        // Amazon RDS / Aurora: <instance>.<account-token>.<region>.rds.amazonaws.com
        if (bare.EndsWith(".rds.amazonaws.com", StringComparison.Ordinal) && labels.Length >= 5)
            return new DatabaseSelfObservation(bare, "Amazon RDS",
                $"rds-{labels[0]}", labels[^4],
                $"Read from the database host this process is connected to ({bare}).");

        // Azure Database for PostgreSQL: <server>.postgres.database.azure.com — the region is not
        // in the host, so it is reported as unknown rather than guessed from the server name.
        if (bare.EndsWith(".postgres.database.azure.com", StringComparison.Ordinal))
            return new DatabaseSelfObservation(bare, "Azure Database for PostgreSQL",
                $"azure-{labels[0]}", null,
                $"The host ({bare}) names the server but not its region, so the region still has to be stated.");

        // Google Cloud SQL and anything self-hosted: a name, and nothing reliable about where it is.
        return new DatabaseSelfObservation(bare, null,
            LooksLikeAnAddress(bare) ? null : $"db-{labels[0]}", null,
            $"The database host is {bare}. Its shape is not one this deployment can read a provider "
            + "or a region from, so both have to be stated.");
    }

    /// <summary>
    /// The host, out of whatever shape the provider chose to describe its connection in.
    ///
    /// <para><b>The defect this fixes.</b> This was <c>DataSource.Split(':')[0]</c>, which is right
    /// for a bare <c>host:5432</c> and catastrophic for what Npgsql actually returns on a pooled
    /// connection: <c>tcp://ep-super-sea-admna6dt.c-2.us-east-1.aws.neon.tech:5432</c>. The split
    /// took the SCHEME. Production was told its database was called <c>db-tcp</c>, in an unreadable
    /// region, and the operator was asked to correct it by hand — which is the exact experience
    /// this whole feature exists to remove, delivered with more confidence than the form it
    /// replaced. A wrong answer offered for confirmation is worse than no answer, because a
    /// plausible-looking one gets confirmed.</para>
    ///
    /// <para>Returns null rather than a fragment when there is no host to be had. The connection
    /// STRING is deliberately never read here: it carries the password, and a parser that has never
    /// seen a credential cannot leak one.</para>
    /// </summary>
    private static string? HostOf(string dataSource)
    {
        var value = dataSource.Trim();

        // A scheme means the rest is a URI, and the host is a component of it rather than the text
        // before the first colon.
        var schemeAt = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0) value = value[(schemeAt + 3)..];

        // Multi-host connection strings list alternates; the first is the one in hand.
        value = value.Split(',')[0];

        // Anything after the authority — a database path, a query — is not the host.
        var pathAt = value.IndexOfAny(['/', '?']);
        if (pathAt >= 0) value = value[..pathAt];

        // user:password@host, when a URI carried credentials. Everything before the last @ is not
        // the host and is not this method's business.
        var credentialsAt = value.LastIndexOf('@');
        if (credentialsAt >= 0) value = value[(credentialsAt + 1)..];

        // IPv6 literals are bracketed, and their colons are not port separators.
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 1 ? value[1..close].ToLowerInvariant() : null;
        }

        // Now, and only now, a colon means a port.
        var portAt = value.IndexOf(':');
        if (portAt >= 0) value = value[..portAt];

        value = value.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Whether the host names a machine rather than a database anybody could refer to later.
    ///
    /// <para>An IP, "localhost", or a single unqualified label — a Docker service called
    /// <c>postgres</c>, a Kubernetes service called <c>db</c>. The registry would happily accept
    /// "db-postgres" as this deployment's opaque provider reference and it would mean nothing to
    /// the auditor who reads it six months later, so nothing is offered and the operator names it.
    /// The single-label case is here because of what a truncated one cost once: the parser handed
    /// the console "db-tcp" — from a connection URI's scheme — and the console offered it for
    /// confirmation in its own confident voice. A suggestion is only worth making when being wrong
    /// about it is obvious to the person confirming it.</para>
    /// </summary>
    private static bool LooksLikeAnAddress(string host) =>
        host is "localhost" or "127.0.0.1" or "::1"
        || !host.Contains('.')
        || host.All(c => char.IsDigit(c) || c == '.')
        || host.Contains(':');
}
