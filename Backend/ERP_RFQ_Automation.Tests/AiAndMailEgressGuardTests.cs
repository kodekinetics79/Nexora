using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Llm;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// EVERY OUTBOUND SOCKET THIS SERVER OPENS TO A NAME SOMEBODY ELSE CHOSE.
///
/// <para>Four defects, one shape. A destination is supplied by a tenant administrator or by
/// deployment configuration, the server dials it, and the thing that was supposed to decide
/// whether that was allowed either was not consulted (the IMAP poller), was consulted once
/// on a string instead of on an address (the AI endpoint classification), or could only ever
/// waive a ratio rather than refuse (the AI allow-list).</para>
///
/// <para>These tests fail if any of the four is reverted.</para>
/// </summary>
public sealed class AiAndMailEgressGuardTests
{
    /// <summary>
    /// Addresses no mail host and no external inference endpoint may ever be dialled on.
    /// These are the SSRF targets, not configuration — they are literal here because they
    /// are the subject of the assertion.
    /// </summary>
    public static TheoryData<string> ProhibitedAddresses => new()
    {
        "127.0.0.1",        // loopback: every service bound to the box itself
        "169.254.169.254",  // cloud instance metadata
        "10.0.0.1",         // RFC 1918
        "172.16.0.1",       // RFC 1918
        "192.168.1.1",      // RFC 1918
        "::1",              // IPv6 loopback
        "::ffff:127.0.0.1"  // IPv4-mapped IPv6 loopback — the unwrap case
    };

    // ================= Defect 1: the IMAP poller bypassed the endpoint policy ==========

    [Theory]
    [MemberData(nameof(ProhibitedAddresses))]
    public async Task AMailHostOnAPrivateOrLoopbackAddress_IsRefusedBeforeASocketIsOpened(string host)
    {
        // The mailbox row's host is tenant-administrator-supplied. Refusal must happen at
        // resolution, not at the far end: nothing is dialled, so connect timing leaks nothing
        // about what is listening.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MailEndpointPolicy.ConnectAsync(host, 143, CancellationToken.None));
        Assert.Contains("prohibited address", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANameResolvingToAnyPrivateAddress_IsRefusedEvenWhenItAlsoResolvesPublic()
    {
        // ALL, not ANY. A name answering with one public and one private address would
        // otherwise be dialled on whichever the OS returned first — which is the whole
        // rebinding trick, and it needs no rebinding at all if a mixed answer is accepted.
        var mixed = new[] { IPAddress.Parse("198.51.100.7"), IPAddress.Loopback };
        Assert.Throws<InvalidOperationException>(
            () => MailEndpointPolicy.ValidateResolvedAddresses(mixed));

        Assert.Throws<InvalidOperationException>(
            () => MailEndpointPolicy.ValidateResolvedAddresses(Array.Empty<IPAddress>()));
    }

    [Fact]
    public void TheImapPoller_TakesItsSocketFromTheEndpointPolicy_NeverFromTheRawHost()
    {
        // The defect was literally `client.ConnectAsync(config.Host, config.Port, …)` — the
        // MailKit overload that resolves and dials the name itself. Every ConnectAsync on a
        // mail client in this file must now be the socket overload, whose socket came from
        // MailEndpointPolicy. Reverting either call site fails this.
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Backend/ERP_RFQ_Automation/Services/EmailService.cs"));

        Assert.DoesNotContain("client.ConnectAsync(config.Host", source, StringComparison.Ordinal);

        var clientConnects = Regex.Matches(source, @"client\.ConnectAsync\(\s*(?<first>[A-Za-z_][A-Za-z0-9_]*)");
        Assert.NotEmpty(clientConnects);
        foreach (Match connect in clientConnects)
            Assert.EndsWith("ocket", connect.Groups["first"].Value, StringComparison.Ordinal);

        // …and every one of those sockets is obtained from the policy, not from a bare Socket.
        Assert.Equal(
            clientConnects.Count,
            Regex.Matches(source, @"MailEndpointPolicy\s*\.ConnectAsync\(").Count);
    }

    [Fact]
    public void NoMailOrInferenceConnectBypassesAPolicy()
    {
        // The sweep, pinned. Every outbound connect in the backend that takes a host from a
        // tenant row or from configuration goes through MailEndpointPolicy (mail, AI) or is
        // an operator-pinned appliance address (the ClamAV scanner, whose host is deployment
        // configuration and is REQUIRED to be loopback on the single-box deployment, so the
        // public-address rule would be exactly wrong for it).
        var backend = Path.Combine(FindRepositoryRoot(), "Backend/ERP_RFQ_Automation");
        var offenders = Directory
            .EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.EndsWith("MalwareScanners.cs", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path), @"client\.ConnectAsync\(\s*(config|smtp|request)\.Host"))
            .ToList();

        Assert.Empty(offenders);
    }

    // ============ Defect 2: "everything stays on this machine" was a string test =======

    [Fact]
    public void ANameThatOnlyLooksLikeLoopback_IsExternalOnceItIsActuallyResolved()
    {
        // Uri.IsLoopback is true for the bare names "localhost" and "loopback" with no lookup
        // whatsoever. A hosts entry, a DNS search domain or a rebinding answer therefore used
        // to produce a deployment that logged "LOCAL … no third-party egress" while every
        // document left the building.
        foreach (var name in new[] { "localhost", "loopback" })
        {
            var offBox = AiProviderEndpoint.Describe(
                "Ollama", $"http://{name}:11434", "m",
                new StagedResolver(IPAddress.Parse("198.51.100.7")));

            Assert.Equal(AiProviderClass.External, offBox.ProviderClass);
            Assert.Equal(AiProviderEndpointReasons.EndpointNameResolvesOffHost, offBox.ClassificationReason);
        }
    }

    [Fact]
    public void ANameResolvingToOneLoopbackAndOneOffBoxAddress_IsExternal()
    {
        var descriptor = AiProviderEndpoint.Describe(
            "Ollama", "http://localhost:11434", "m",
            new StagedResolver(IPAddress.Loopback, IPAddress.Parse("198.51.100.7")));

        Assert.Equal(AiProviderClass.External, descriptor.ProviderClass);
        Assert.Equal(AiProviderEndpointReasons.EndpointNameResolvesOffHost, descriptor.ClassificationReason);
    }

    [Fact]
    public void ANameThatCannotBeResolvedAtAll_FailsClosedToExternal()
    {
        var descriptor = AiProviderEndpoint.Describe(
            "Ollama", "http://localhost:11434", "m", new ThrowingResolver());

        Assert.Equal(AiProviderClass.External, descriptor.ProviderClass);
        Assert.Equal(AiProviderEndpointReasons.EndpointNameResolutionFailed, descriptor.ClassificationReason);
    }

    [Fact]
    public void AGenuinelyLoopbackDeployment_IsStillLocal()
    {
        // The guarantee the single-box deployment sells has to keep working: a name that
        // really does resolve only to this machine, and the loopback literals, stay Local.
        var byName = AiProviderEndpoint.Describe(
            "Ollama", "http://localhost:11434", "m",
            new StagedResolver(IPAddress.Loopback, IPAddress.IPv6Loopback));
        Assert.Equal(AiProviderClass.Local, byName.ProviderClass);
        Assert.Equal(AiProviderEndpointReasons.LoopbackEndpoint, byName.ClassificationReason);

        var byLiteral = AiProviderEndpoint.Describe(
            "Ollama", $"http://{IPAddress.Loopback}:11434", "m", new ThrowingResolver());
        Assert.Equal(AiProviderClass.Local, byLiteral.ProviderClass);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://provider.example/")]
    [InlineData("https://user:secret@provider.example/")]
    public void TheFailClosedBehavioursThatWereAlreadyRight_Survive(string? configured)
    {
        var descriptor = AiProviderEndpoint.Describe("Ollama", configured, "m", new ThrowingResolver());
        Assert.Equal(AiProviderClass.External, descriptor.ProviderClass);
        Assert.False(descriptor.IsResolved);
    }

    [Theory]
    [MemberData(nameof(ProhibitedAddresses))]
    public async Task AnEndpointClassifiedExternal_IsNeverDialledOnAPrivateAddress(string address)
    {
        // The mirror of the mail rule. An authorized third-party origin that starts answering
        // with 169.254.169.254 would otherwise turn the inference client into an SSRF
        // primitive with the document body as the request.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AiEgressGuard.ConnectAsync(AiProviderClass.External, address, 443, CancellationToken.None));
        Assert.Equal(AiEgressGuard.ExternalEndpointIsNotPublicMessage, refusal.Message);
    }

    [Fact]
    public async Task AnEndpointClassifiedLocal_IsNeverDialledOffThisMachine()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AiEgressGuard.ConnectAsync(
                AiProviderClass.Local, "198.51.100.7", 11434, CancellationToken.None));
        Assert.Equal(AiEgressGuard.LocalEndpointLeftTheHostMessage, refusal.Message);
    }

    [Fact]
    public async Task ARedirectOffTheLoopbackService_IsNotFollowed()
    {
        // A 307/308 re-sends the METHOD AND THE BODY. For an inference client the body is the
        // customer's document text, so a redirect the client obeys is an egress no
        // classification saw and no allow-list row authorized.
        using var origin = new OneShotHttpOrigin(
            "HTTP/1.1 307 Temporary Redirect\r\n"
            + "Location: http://198.51.100.7/v1/chat\r\n"
            + "Content-Length: 0\r\nConnection: close\r\n\r\n");

        using var client = new HttpClient(AiEgressGuard.CreateHandler(() => AiProviderClass.Local));
        using var response = await client.PostAsync(
            origin.Uri, new StringContent("{\"document\":\"confidential\"}", Encoding.UTF8, "application/json"));

        // The redirect is surfaced, never obeyed: exactly one connection was made, and it was
        // to the loopback service.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(1, origin.RequestCount);
        Assert.Contains("confidential", origin.LastRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void BothAiHttpClientsRefuseRedirects()
    {
        var handler = AiEgressGuard.CreateHandler(() => AiProviderClass.Local);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);

        // The agent's registration is the one that can be exercised through real DI.
        var services = new ServiceCollection();
        services.AddAgentEngine(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agent:Anthropic:ApiKey"] = "unit-test-key" }).Build());
        using var provider = services.BuildServiceProvider();

        // AddHttpClient<TClient, TImplementation> names the client after TClient.
        var primary = PrimaryHandlerFor(provider, nameof(IAgentLlm));
        var sockets = Assert.IsType<SocketsHttpHandler>(primary);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.NotNull(sockets.ConnectCallback);

        // Program.cs builds the extraction client the same way. The registration is not
        // reachable from a test without booting the host, so the wiring is pinned at source.
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Backend/ERP_RFQ_Automation/Program.cs"));
        var ollamaRegistration = program[program.IndexOf("AddHttpClient<OllamaLlmService>", StringComparison.Ordinal)..];
        Assert.Contains("ConfigurePrimaryHttpMessageHandler",
            ollamaRegistration[..1200], StringComparison.Ordinal);
        Assert.Contains("AiEgressGuard.CreateHandler", ollamaRegistration[..1200], StringComparison.Ordinal);
    }

    // ============ Defect 4: the Anthropic origin is inside the endpoint model ==========

    [Fact]
    public void TheAgentsDestinationIsANamedDescriptorThatAnAllowListRowCanMatch()
    {
        var resolver = new AiProviderEndpointResolver(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>()).Build(),
            new NoopLogger<AiProviderEndpointResolver>());

        var anthropic = Assert.Single(resolver.All.Where(
            x => AiProviderEndpoint.ProviderMatches(x.Provider, AnthropicProviderDefaults.Provider)));

        Assert.True(anthropic.IsResolved);
        Assert.Equal(AiProviderClass.External, anthropic.ProviderClass);
        // A normalised ORIGIN: no path, so one authorization covers one destination.
        Assert.Equal(
            AnthropicProviderDefaults.DefaultBaseUrl.ToLowerInvariant().TrimEnd('/'), anthropic.Endpoint);
        Assert.Same(anthropic, resolver.Find(AnthropicProviderDefaults.Provider, anthropic.Model));
        Assert.Null(resolver.Find(AnthropicProviderDefaults.Provider, "a-model-this-process-never-calls"));
    }

    [Fact]
    public void TheAgentClientPostsToTheOriginItsDescriptorNames()
    {
        // The URL and the descriptor are derived from the same configuration value, so an
        // operator authorizing the origin is authorizing the origin that is actually dialled.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();

        var requestUri = new Uri(AnthropicProviderDefaults.MessagesUrl(configuration));
        var descriptor = AiProviderEndpoint.Describe(
            AnthropicProviderDefaults.Provider,
            AnthropicProviderDefaults.BaseUrl(configuration),
            AnthropicProviderDefaults.Model(configuration));

        Assert.Equal(descriptor.Endpoint, $"{requestUri.Scheme}://{requestUri.IdnHost}");
        Assert.Equal(AnthropicProviderDefaults.MessagesPath, requestUri.AbsolutePath);
    }

    // ================= Defect 5a: a null ambient tenant is a denial ====================

    [Fact]
    public async Task AnEvaluationWithNoAmbientTenant_IsDenied()
    {
        // The background-worker case: the EF global filter is a no-op and the connection is
        // BYPASSRLS, so a hand-written predicate was the only isolation left. The gate now
        // refuses rather than relying on it.
        using var database = new TestDb();
        const long tenantId = 77_001;
        using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantId);
            var policy = AiProcessingPolicy.CreateSecureDefault(tenantId, "test", DateTime.UtcNow);
            policy.ExternalProcessingAllowed = true;
            seed.AiProcessingPolicies.Add(policy);
            seed.SaveChanges();
        }

        var resolver = ExternalResolver();
        using var db = database.ContextFor(tenantId);
        var trust = new AiExternalProviderTrustService(
            db, new StubTenant(null), resolver, new NoopLogger<AiExternalProviderTrustService>());

        var decision = await trust.EvaluateAsync(
            tenantId, resolver.Current, AiPurposes.RfqExtraction, unstructuredPayload: false, default);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.TenantMismatch, decision.Reason);
    }

    // ================= Defect 5c: EgressPolicy is a reader, not a note ==================

    [Fact]
    public async Task RedactedFieldsOnly_RefusesWholeDocumentEgressEvenToAnAuthorizedEndpoint()
    {
        using var database = new TestDb();
        const long tenantId = 77_002;
        var resolver = ExternalResolver();

        using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantId);
            var policy = AiProcessingPolicy.CreateSecureDefault(tenantId, "test", DateTime.UtcNow);
            policy.ExternalProcessingAllowed = true;
            seed.AiProcessingPolicies.Add(policy);
            seed.AiExternalProviderAuthorizations.Add(new AiExternalProviderAuthorization
            {
                BusinessUnitId = tenantId,
                Provider = resolver.Current.Provider,
                Endpoint = resolver.Current.Endpoint,
                Model = AiProviderEndpoint.AnyModel,
                AllowedPurposes = AiPurposes.RfqExtraction,
                UnstructuredDocumentsAllowed = true,
                Justification = "Test authorization.",
                AuthorizedByUserId = 1,
                AuthorizedBy = "user:1",
                AuthorizedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            });
            seed.SaveChanges();
        }

        using var db = database.ContextFor(tenantId);
        var trust = new AiExternalProviderTrustService(
            db, new StubTenant(tenantId), resolver, new NoopLogger<AiExternalProviderTrustService>());

        // The secure default is RedactedFieldsOnly. The destination grant says unstructured is
        // fine; the tenant's own policy says its data never becomes whole-document egress.
        // Both must agree.
        var refused = await trust.EvaluateAsync(
            tenantId, resolver.Current, AiPurposes.RfqExtraction, unstructuredPayload: true, default);
        Assert.False(refused.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.EgressPolicyForbidsWholeDocuments, refused.Reason);

        // Field/row payloads are unaffected — the policy governs whole documents, not egress
        // in general.
        var structured = await trust.EvaluateAsync(
            tenantId, resolver.Current, AiPurposes.RfqExtraction, unstructuredPayload: false, default);
        Assert.True(structured.Allowed);

        using (var relax = database.ContextFor(null))
        {
            var policy = relax.AiProcessingPolicies.IgnoreQueryFilters().Single(x => x.BusinessUnitId == tenantId);
            policy.EgressPolicy = AiEgressPolicies.FullDocument;
            relax.SaveChanges();
        }

        using var reread = database.ContextFor(tenantId);
        var permitted = await new AiExternalProviderTrustService(
                reread, new StubTenant(tenantId), resolver, new NoopLogger<AiExternalProviderTrustService>())
            .EvaluateAsync(tenantId, resolver.Current, AiPurposes.RfqExtraction, true, default);
        Assert.True(permitted.Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("anything-an-operator-typed")]
    public void AnUnrecognisedEgressPolicyValue_ReadsAsTheStrictOne(string value)
    {
        Assert.False(AiEgressPolicies.PermitsWholeDocument(value));
        Assert.False(AiEgressPolicies.IsRecognised(value));
    }

    // ---------------------------------------------------------------- helpers

    private static AiProviderEndpointResolver ExternalResolver() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "https://inference.example/",
            ["Ollama:Model"] = "test-model"
        }).Build(),
        new NoopLogger<AiProviderEndpointResolver>());

    private static HttpMessageHandler PrimaryHandlerFor(IServiceProvider provider, string clientName)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName);
        var builder = new CapturingHandlerBuilder(provider) { Name = clientName };
        foreach (var configure in options.HttpMessageHandlerBuilderActions)
            configure(builder);
        return builder.PrimaryHandler;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    /// <summary>
    /// Stands in for the builder the HttpClientFactory hands to each registered
    /// <c>HttpMessageHandlerBuilderActions</c> delegate, so a test can run those delegates and
    /// inspect the primary handler they install. <see cref="Services"/> is get-only on the base
    /// type, so it is overridden rather than assigned — the registered delegates resolve the
    /// egress-guarding handler out of it, so it must be the real provider.
    /// </summary>
    private sealed class CapturingHandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override IServiceProvider Services { get; } = services;
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    /// <summary>A resolver that answers with exactly the addresses a test wants to stage —
    /// the hosts-file / search-domain / rebinding answer real DNS cannot be asked for.</summary>
    private sealed class StagedResolver(params IPAddress[] addresses) : IAiEndpointHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) => addresses;
    }

    private sealed class ThrowingResolver : IAiEndpointHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) =>
            throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// A minimal HTTP origin on loopback that answers every request with one canned response.
    /// Used instead of a mock handler because the behaviour under test — that the transport
    /// refuses to follow a redirect — lives in the real handler, below anywhere a mock could
    /// be injected.
    /// </summary>
    private sealed class OneShotHttpOrigin : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serving;
        private int _requestCount;

        public OneShotHttpOrigin(string response)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Uri = new Uri($"http://{IPAddress.Loopback}:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/chat");
            _serving = ServeAsync(Encoding.ASCII.GetBytes(response), _stopping.Token);
        }

        public Uri Uri { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public string LastRequest { get; private set; } = string.Empty;

        private async Task ServeAsync(byte[] response, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct);
                    await using var stream = client.GetStream();
                    LastRequest = await ReadRequestAsync(stream, ct);
                    Interlocked.Increment(ref _requestCount);
                    await stream.WriteAsync(response, ct);
                    await stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }

        /// <summary>Reads headers, then exactly the declared body — one ReadAsync is not
        /// guaranteed to deliver both, and a partial read would make the assertion flaky.</summary>
        private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[16384];
            var total = 0;
            var headerEnd = -1;
            var contentLength = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
                if (read == 0) break;
                total += read;
                var text = Encoding.UTF8.GetString(buffer, 0, total);
                if (headerEnd < 0)
                {
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd < 0) continue;
                    var header = text[..headerEnd];
                    var marker = header.IndexOf("Content-Length:", StringComparison.OrdinalIgnoreCase);
                    if (marker >= 0)
                        int.TryParse(
                            header[(marker + "Content-Length:".Length)..].Split('\r')[0].Trim(),
                            out contentLength);
                }
                if (total >= headerEnd + 4 + contentLength) break;
            }
            return Encoding.UTF8.GetString(buffer, 0, total);
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            try { _serving.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            _stopping.Dispose();
        }
    }
}
