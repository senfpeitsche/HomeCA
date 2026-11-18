using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HomeCA.Service.Automation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Endpoints;
using HomeCA.Service.Operations;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using HomeCA.Service.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class IssuanceTokenTests : IDisposable
{
    private readonly TestFixture _fixture = new();
    private string TokenFile => Path.Combine(_fixture.RootPath, "state", "issuance-tokens.json");

    // ── Registry ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Created_secret_is_returned_once_and_only_its_hash_is_stored()
    {
        var registry = CreateRegistry();
        var created = await registry.CreateAsync(new CreateIssuanceTokenRequest("abnahme-vm", ["Abnahme.Home.Lan."]), "admin", CancellationToken.None);

        Assert.StartsWith(IssuanceTokenRegistry.SecretPrefix, created.Secret);
        Assert.Equal(["abnahme.home.lan"], created.Token.AllowedDnsSuffixes);
        Assert.Equal(["TLS"], created.Token.AllowedUsages);
        Assert.Equal(30, created.Token.MaxValidityDays);
        var stored = await File.ReadAllTextAsync(TokenFile);
        Assert.DoesNotContain(created.Secret, stored);
        Assert.Contains(IssuanceTokenRegistry.HashSecret(created.Secret), stored);
        Assert.Equal(created.Token.Id, (await registry.ResolveAsync(created.Secret, CancellationToken.None))?.Id);
        Assert.Null(await registry.ResolveAsync(created.Secret + "x", CancellationToken.None));
        Assert.Null(await registry.ResolveAsync("not-a-token", CancellationToken.None));
    }

    [Theory]
    [InlineData("", "a.lan", 30)]
    [InlineData("vm", "lan", 30)]
    [InlineData("vm", "not a name", 30)]
    [InlineData("vm", "a.lan", 0)]
    [InlineData("vm", "a.lan", 731)]
    public async Task Invalid_tokens_are_rejected(string name, string suffix, int maxValidity) =>
        await Assert.ThrowsAsync<ArgumentException>(() => CreateRegistry().CreateAsync(new CreateIssuanceTokenRequest(name, [suffix], MaxValidityDays: maxValidity), null, CancellationToken.None));

    [Fact]
    public async Task Last_use_is_written_at_most_once_per_minute()
    {
        var registry = CreateRegistry();
        var token = (await registry.CreateAsync(new CreateIssuanceTokenRequest("vm", ["a.lan"]), null, CancellationToken.None)).Token;
        var first = DateTimeOffset.UtcNow;

        await registry.TouchAsync(token.Id, first, CancellationToken.None);
        await registry.TouchAsync(token.Id, first.AddSeconds(30), CancellationToken.None);
        Assert.Equal(first, (await registry.GetAsync(token.Id, CancellationToken.None))!.LastUsedAt);

        await registry.TouchAsync(token.Id, first.AddSeconds(61), CancellationToken.None);
        Assert.Equal(first.AddSeconds(61), (await registry.GetAsync(token.Id, CancellationToken.None))!.LastUsedAt);
    }

    [Fact]
    public void Backups_include_the_token_state_file()
    {
        var storage = _fixture.CreateStorage();
        File.WriteAllText(TokenFile, "[]");
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(storage.CreateArchive(CancellationToken.None)));
        Assert.NotNull(archive.GetEntry("state/issuance-tokens.json"));
    }

    // ── Scope ─────────────────────────────────────────────────────────────────

    private static IssuanceToken Scope(int maxValidity = 30, string[]? cidrs = null, string[]? usages = null) =>
        new("t1", "vm", ["abnahme.home.lan", "xn--bcher-kva.lab"], cidrs ?? [], usages ?? ["TLS"], maxValidity, null, DateTimeOffset.UtcNow, "admin", null, null);

    [Theory]
    [InlineData("abnahme.home.lan")]
    [InlineData("mp.abnahme.home.lan")]
    [InlineData("MP.Abnahme.Home.Lan.")]
    [InlineData("*.abnahme.home.lan")]
    [InlineData("a.bücher.lab")]
    public void Names_within_scope_are_allowed(string name) =>
        Assert.True(IssuanceTokenScope.Check(Scope(), "TLS", [name], [], 2).Allowed);

    [Theory]
    [InlineData("mail.home.lan")]
    [InlineData("evilabnahme.home.lan")]
    [InlineData("abnahme.home.lan.attacker.tld")]
    [InlineData("*.home.lan")]
    [InlineData("a.*.abnahme.home.lan")]
    [InlineData("home.lan")]
    [InlineData("")]
    public void Names_outside_scope_are_denied_and_named(string name)
    {
        var result = IssuanceTokenScope.Check(Scope(), "TLS", ["mp.abnahme.home.lan", name], [], 2);
        Assert.False(result.Allowed);
        Assert.Contains($"'{name}'", result.Violation);
    }

    [Fact]
    public void Names_are_normalized_for_issuance()
    {
        var result = IssuanceTokenScope.Check(Scope(), "TLS", ["MP.Abnahme.Home.Lan.", "mp.abnahme.home.lan", "a.bücher.lab"], [], 2);
        Assert.Equal(["mp.abnahme.home.lan", "a.xn--bcher-kva.lab"], result.DnsNames);
    }

    [Fact]
    public void Ip_sans_need_an_allowed_network()
    {
        Assert.False(IssuanceTokenScope.Check(Scope(), "TLS", [], ["10.0.0.5"], 2).Allowed);
        Assert.True(IssuanceTokenScope.Check(Scope(cidrs: ["10.0.0.0/24"]), "TLS", [], ["10.0.0.5"], 2).Allowed);
        Assert.False(IssuanceTokenScope.Check(Scope(cidrs: ["10.0.0.0/24"]), "TLS", [], ["10.0.1.5"], 2).Allowed);
        Assert.False(IssuanceTokenScope.Check(Scope(cidrs: ["10.0.0.0/24"]), "TLS", [], ["not-an-ip"], 2).Allowed);
    }

    [Fact]
    public void Validity_and_usage_are_limited()
    {
        Assert.True(IssuanceTokenScope.Check(Scope(), "TLS", ["mp.abnahme.home.lan"], [], 30).Allowed);
        Assert.Contains("365", IssuanceTokenScope.Check(Scope(), "TLS", ["mp.abnahme.home.lan"], [], 365).Violation);
        Assert.False(IssuanceTokenScope.Check(Scope(), "mTLS", ["mp.abnahme.home.lan"], [], 2).Allowed);
        Assert.True(IssuanceTokenScope.Check(Scope(usages: ["TLS", "mTLS"]), "mTLS", ["mp.abnahme.home.lan"], [], 2).Allowed);
    }

    [Fact]
    public void Csr_names_include_the_common_name_and_every_san()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (dns, ips) = IssuanceTokenScope.CsrNames(CreateCsr("CN=intranet.home.lan", key, ["mp.abnahme.home.lan"], ["10.0.0.9"]));
        Assert.Equal(["mp.abnahme.home.lan", "intranet.home.lan"], dns);
        Assert.Equal(["10.0.0.9"], ips);
        Assert.Throws<ArgumentException>(() => IssuanceTokenScope.CsrNames("not a csr"));
    }

    // ── Issuance API ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Token_issues_within_scope_and_sees_only_its_own_certificates()
    {
        var env = await SetupAsync();
        var (token, http) = await CallerAsync(env, "abnahme", ["abnahme.home.lan"]);
        var (other, otherHttp) = await CallerAsync(env, "other", ["other.lan"]);

        var issued = await IssueAsync(env, http, new IssueCertificateApiRequest("TLS", ["mp.abnahme.home.lan"], [], 2));
        var result = Assert.IsAssignableFrom<IValueHttpResult>(issued).Value as IssueResult;
        Assert.NotNull(result);
        Assert.Equal(token.Id, (await env.Certificates.FindAsync(result.Id, CancellationToken.None))?.IssuedByTokenId);
        Assert.Empty(await env.Plans.ListAsync(CancellationToken.None));
        var adminCertificate = await env.Certificates.IssueAsync(new IssueCertificateRequest("TLS", ["admin.abnahme.home.lan"], [], 30), CancellationToken.None);

        Assert.Equal(200, Status(await IssuanceEndpoints.GetAsync(result.Id, http, env.Certificates, CancellationToken.None)));
        Assert.Equal(404, Status(await IssuanceEndpoints.GetAsync(result.Id, otherHttp, env.Certificates, CancellationToken.None)));
        Assert.Equal(404, Status(await IssuanceEndpoints.GetAsync(adminCertificate.Id, http, env.Certificates, CancellationToken.None)));
        Assert.Equal(404, Status(await IssuanceEndpoints.ExportPfxAsync(adminCertificate.Id, new PfxExportRequest("pw"), http, env.Certificates, env.Storage, env.Administration, CancellationToken.None)));

        var pfx = await IssuanceEndpoints.ExportPfxAsync(result.Id, new PfxExportRequest("secret-pfx-password"), http, env.Certificates, env.Storage, env.Administration, CancellationToken.None);
        Assert.IsType<FileContentHttpResult>(pfx);
        var audit = await env.Administration.ReadAuditLogAsync(0, 100, null, CancellationToken.None);
        Assert.Contains(audit, entry => entry.Action == "issuance-token.issued" && entry.Subject.Contains(result.Id));
        Assert.Contains(audit, entry => entry.Action == "issuance-token.exported" && entry.Subject.Contains(result.Id));
        Assert.All(audit, entry => Assert.DoesNotContain("secret-pfx-password", entry.Subject));
        Assert.All(audit, entry => Assert.DoesNotContain(IssuanceTokenRegistry.SecretPrefix, entry.Subject));
        Assert.NotEqual(token.Id, other.Id);
    }

    [Fact]
    public async Task Out_of_scope_requests_are_denied_with_403_and_audited()
    {
        var env = await SetupAsync();
        var (_, http) = await CallerAsync(env, "abnahme", ["abnahme.home.lan"]);

        var denied = await IssueAsync(env, http, new IssueCertificateApiRequest("TLS", ["mail.home.lan"], [], 2));
        var tooLong = await IssueAsync(env, http, new IssueCertificateApiRequest("TLS", ["mp.abnahme.home.lan"], [], 365));

        Assert.Equal(403, Status(denied));
        Assert.Contains("mail.home.lan", Assert.IsType<ProblemHttpResult>(denied).ProblemDetails.Detail);
        Assert.Equal(403, Status(tooLong));
        Assert.Equal(2, (await env.Administration.ReadAuditLogAsync(0, 100, "issuance-token.denied", CancellationToken.None)).Count);
        Assert.Empty(await env.Certificates.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Csr_signing_checks_every_name_in_the_csr()
    {
        var env = await SetupAsync();
        var (token, http) = await CallerAsync(env, "abnahme", ["abnahme.home.lan"]);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var extraSan = await SignAsync(env, http, new SignCsrRequest(CreateCsr("CN=mp.abnahme.home.lan", key, ["mp.abnahme.home.lan", "intranet.home.lan"], []), ValidityDays: 2));
        // Explicit request names would replace the CSR's, but the CSR's must still be in scope.
        var smuggled = await SignAsync(env, http, new SignCsrRequest(CreateCsr("CN=intranet.home.lan", key, [], []), ValidityDays: 2, DnsNames: ["mp.abnahme.home.lan"]));
        var valid = await SignAsync(env, http, new SignCsrRequest(CreateCsr("CN=mp.abnahme.home.lan", key, ["mp.abnahme.home.lan"], []), ValidityDays: 2));

        Assert.Contains("intranet.home.lan", Assert.IsType<ProblemHttpResult>(extraSan).ProblemDetails.Detail);
        Assert.Equal(403, Status(smuggled));
        var result = Assert.IsAssignableFrom<IValueHttpResult>(valid).Value as IssueResult;
        Assert.NotNull(result);
        Assert.Equal(token.Id, (await env.Certificates.FindAsync(result.Id, CancellationToken.None))?.IssuedByTokenId);
        Assert.Contains(await env.Administration.ReadAuditLogAsync(0, 100, "issuance-token.csr-signed", CancellationToken.None), entry => entry.Subject.Contains(result.Id));
    }

    [Fact]
    public async Task Issuance_requests_per_token_are_rate_limited()
    {
        var env = await SetupAsync(requestsPerHour: 2);
        var (_, http) = await CallerAsync(env, "abnahme", ["abnahme.home.lan"]);
        var (_, otherHttp) = await CallerAsync(env, "other", ["other.lan"]);
        var request = new IssueCertificateApiRequest("TLS", ["mp.abnahme.home.lan"], [], 2);

        Assert.Equal(200, Status(await IssueAsync(env, http, request)));
        Assert.Equal(403, Status(await IssueAsync(env, http, request with { DnsNames = ["mail.home.lan"] })));
        Assert.Equal(429, Status(await IssueAsync(env, http, request)));
        Assert.Equal(200, Status(await IssueAsync(env, otherHttp, request with { DnsNames = ["x.other.lan"] })));
    }

    [Fact]
    public async Task Revoked_expired_and_unknown_tokens_get_401_and_management_rejects_tokens()
    {
        var env = await SetupAsync();
        var created = await env.Tokens.CreateAsync(new CreateIssuanceTokenRequest("vm", ["abnahme.home.lan"]), null, CancellationToken.None);
        var expired = await env.Tokens.CreateAsync(new CreateIssuanceTokenRequest("old", ["abnahme.home.lan"]), null, CancellationToken.None);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(TokenFile))!.AsArray();
        state.Single(node => (string?)node!["Id"] == expired.Token.Id)!["ExpiresAt"] = DateTimeOffset.UtcNow.AddDays(-1);
        await File.WriteAllTextAsync(TokenFile, state.ToJsonString());

        Assert.Equal(200, await FilterAsync(env.Filter, created.Secret));
        Assert.Equal(401, await FilterAsync(env.Filter, expired.Secret));
        Assert.Equal(401, await FilterAsync(env.Filter, "hca_it_unknown"));
        // An administrator session filter never accepts an issuance token.
        Assert.Equal(401, await FilterAsync(new BearerTokenFilter(env.Administration), created.Secret));

        await env.Tokens.RevokeAsync(created.Token.Id, CancellationToken.None);
        Assert.Equal(401, await FilterAsync(env.Filter, created.Secret));
        var denied = await env.Administration.ReadAuditLogAsync(0, 100, "issuance-token.denied", CancellationToken.None);
        Assert.Contains(denied, entry => entry.Subject.Contains("expired"));
        Assert.Contains(denied, entry => entry.Subject.Contains("revoked"));
    }

    [Fact]
    public async Task Invalid_tokens_are_throttled_per_client_address()
    {
        var env = await SetupAsync();
        for (var attempt = 0; attempt < 5; attempt++) Assert.Equal(401, await FilterAsync(env.Filter, "hca_it_guess" + attempt));
        Assert.Equal(429, await FilterAsync(env.Filter, "hca_it_guess"));
    }

    [Fact]
    public async Task Token_issued_certificates_raise_no_expiry_warnings()
    {
        var env = await SetupAsync();
        var (_, http) = await CallerAsync(env, "abnahme", ["abnahme.home.lan"], maxValidity: 30);
        var issued = (IssueResult)Assert.IsAssignableFrom<IValueHttpResult>(await IssueAsync(env, http, new IssueCertificateApiRequest("TLS", ["mp.abnahme.home.lan"], [], 2))).Value!;
        var adminIssued = await env.Certificates.IssueAsync(new IssueCertificateRequest("TLS", ["soon.lab.test"], [], 2), CancellationToken.None);
        var attention = new AttentionService(env.Storage, env.Certificates, env.Authorities, env.Revocations, env.Plans,
            new DeploymentTargetRegistry(env.Storage, NullLogger<DeploymentTargetRegistry>.Instance),
            new BackupScheduleService(env.Storage, null!, NullLogger<BackupScheduleService>.Instance));

        var items = await attention.GetAsync(CancellationToken.None);
        var warnings = new CertificateExpiryService(env.Storage, NullLogger<CertificateExpiryService>.Instance)
            .GetWarnings(excludedIds: await env.Certificates.TokenIssuedIdsAsync(CancellationToken.None));

        Assert.DoesNotContain(items, item => item.ReferenceId == issued.Id);
        Assert.Contains(items, item => item.Kind == "certificate-expiring" && item.ReferenceId == adminIssued.Id);
        Assert.DoesNotContain(warnings, warning => warning.Subject.Contains("mp.abnahme.home.lan"));
        Assert.Contains(warnings, warning => warning.Subject.Contains("soon.lab.test"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record Env(
        Service.Infrastructure.HomeCaStorage Storage, CertificateAuthorityService Authorities, RevocationRegistry Revocations, CertificateIssuanceService Certificates,
        RenewalPlanRegistry Plans, IssuanceTokenRegistry Tokens, LocalAdministrationService Administration, IssuanceTokenFilter Filter);

    private IssuanceTokenRegistry CreateRegistry() => new(_fixture.CreateStorage(), NullLogger<IssuanceTokenRegistry>.Instance);

    private async Task<Env> SetupAsync(int requestsPerHour = 30)
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        var tokens = new IssuanceTokenRegistry(storage, NullLogger<IssuanceTokenRegistry>.Instance);
        var administration = new LocalAdministrationService(storage, new TestHostEnvironment(), NullLogger<LocalAdministrationService>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IssuanceTokens:RequestsPerHour"] = requestsPerHour.ToString() }).Build();
        var filter = new IssuanceTokenFilter(tokens, administration, configuration, NullLogger<IssuanceTokenFilter>.Instance);
        return new Env(storage, authorities, revocations, certificates, new RenewalPlanRegistry(storage, NullLogger<RenewalPlanRegistry>.Instance), tokens, administration, filter);
    }

    private static async Task<(IssuanceToken Token, HttpContext Http)> CallerAsync(Env env, string name, string[] suffixes, int maxValidity = 30)
    {
        var created = await env.Tokens.CreateAsync(new CreateIssuanceTokenRequest(name, suffixes, MaxValidityDays: maxValidity), null, CancellationToken.None);
        var http = new DefaultHttpContext();
        http.Items[IssuanceTokenFilter.TokenItemKey] = created.Token;
        return (created.Token, http);
    }

    private static Task<IResult> IssueAsync(Env env, HttpContext http, IssueCertificateApiRequest request) =>
        IssuanceEndpoints.IssueAsync(request, http, env.Certificates, env.Filter, env.Administration, CancellationToken.None);

    private static Task<IResult> SignAsync(Env env, HttpContext http, SignCsrRequest request) =>
        IssuanceEndpoints.SignCsrAsync(request, http, env.Certificates, env.Filter, env.Administration, CancellationToken.None);

    /// <summary>Runs an endpoint filter for a request carrying <paramref name="secret"/>; 200 means it reached the endpoint.</summary>
    private static async Task<int> FilterAsync(IEndpointFilter filter, string secret)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/issuance/certificates";
        http.Request.Headers.Authorization = "Bearer " + secret;
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>(Results.Ok()));
        return Status((IResult)result!);
    }

    private static int Status(IResult result) => result switch
    {
        IStatusCodeHttpResult { StatusCode: { } code } => code,
        IFileHttpResult => 200,
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
    };

    private static string CreateCsr(string subject, ECDsa key, string[] dnsNames, string[] ipAddresses)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        if (dnsNames.Length > 0 || ipAddresses.Length > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames) san.AddDnsName(name);
            foreach (var address in ipAddresses) san.AddIpAddress(System.Net.IPAddress.Parse(address));
            request.CertificateExtensions.Add(san.Build());
        }
        return request.CreateSigningRequestPem();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "HomeCA.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose() => _fixture.Dispose();
}
