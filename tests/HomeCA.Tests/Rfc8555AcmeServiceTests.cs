using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeCA.Service.Acme;
using HomeCA.Service.Deployments;
using HomeCA.Service.Domains;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class Rfc8555AcmeServiceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task VerifyJws_Accepts_Signed_Request_Only_Once()
    {
        var acme = await CreateServiceAsync();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expectedUrl = "https://homeca.test/acme/new-acct";
        var nonce = acme.CreateNonce();
        var body = CreateSignedJws(signingKey, nonce, expectedUrl, "{}", out var jwk);

        var verified = acme.VerifyJws(body, expectedUrl);

        Assert.Equal("{}", Encoding.UTF8.GetString(verified.Payload));
        Assert.Equal(Rfc8555AcmeService.ComputeJwkThumbprint(jwk), verified.Thumbprint);

        var replay = Assert.Throws<AcmeProblemException>(() => acme.VerifyJws(body, expectedUrl));
        Assert.Equal("badNonce", replay.ProblemType);
    }

    [Fact]
    public async Task VerifyJws_Accepts_Signature_With_Leading_Zero_Component()
    {
        var acme = await CreateServiceAsync();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expectedUrl = "https://homeca.test/acme/new-acct";

        // About 1 in 128 ES256 signatures has r or s starting with 0x00; search for one deterministically.
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var body = CreateSignedJws(signingKey, acme.CreateNonce(), expectedUrl, $"{{\"n\":{attempt}}}", out _);
            var signature = Rfc8555AcmeService.Base64UrlDecode(JsonNode.Parse(body)!["signature"]!.GetValue<string>());
            if (signature[0] != 0 && signature[32] != 0) continue;

            var verified = acme.VerifyJws(body, expectedUrl);

            Assert.Equal($"{{\"n\":{attempt}}}", Encoding.UTF8.GetString(verified.Payload));
            return;
        }
        Assert.Fail("No signature with a leading zero component was produced.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"protected\":\"%%%\",\"payload\":\"\",\"signature\":\"\"}")]
    [InlineData("{\"protected\":42,\"payload\":\"\",\"signature\":\"\"}")]
    public async Task VerifyJws_Reports_Broken_Requests_As_Malformed(string body)
    {
        var acme = await CreateServiceAsync();

        var problem = Assert.Throws<AcmeProblemException>(() => acme.VerifyJws(Encoding.UTF8.GetBytes(body), "https://homeca.test/acme/new-acct"));

        Assert.Equal("malformed", problem.ProblemType);
    }

    [Fact]
    public async Task Order_Offers_Http01_Challenge()
    {
        var acme = await CreateServiceAsync();
        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jwk = CreateJwk(accountKey);
        var account = await acme.NewAccountAsync(jwk, "admin@example.test", CancellationToken.None);

        var order = await acme.NewOrderAsync(account.Id, [new Rfc8555Identifier("dns", "host.example.test")], CancellationToken.None);
        var challenge = Assert.Single(Assert.Single(order.Authorizations).Challenges);

        Assert.Equal("http-01", challenge.Type);
        Assert.Equal("pending", challenge.Status);
        Assert.Matches("^[A-Za-z0-9_-]+$", challenge.Token);
    }

    [Fact]
    public async Task VerifyAccountRequest_Accepts_Signature_Of_Account_Key()
    {
        var acme = await CreateServiceAsync();
        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var account = await acme.NewAccountAsync(CreateJwk(accountKey), null, CancellationToken.None);
        var url = "https://homeca.test/acme/order/x/finalize";
        var body = CreateKidSignedJws(accountKey, acme.CreateNonce(), url, "{}", $"https://homeca.test/acme/acct/{account.Id}");

        var (_, verifiedAccount) = await acme.VerifyAccountRequestAsync(body, url, CancellationToken.None);

        Assert.Equal(account.Id, verifiedAccount.Id);
    }

    [Fact]
    public async Task VerifyAccountRequest_Rejects_Kid_Request_Signed_By_Foreign_Key()
    {
        var acme = await CreateServiceAsync();
        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var account = await acme.NewAccountAsync(CreateJwk(accountKey), null, CancellationToken.None);
        var url = "https://homeca.test/acme/order/x/finalize";
        var body = CreateKidSignedJws(attackerKey, acme.CreateNonce(), url, "{}", $"https://homeca.test/acme/acct/{account.Id}");

        var problem = await Assert.ThrowsAsync<AcmeProblemException>(() => acme.VerifyAccountRequestAsync(body, url, CancellationToken.None));

        Assert.Equal("unauthorized", problem.ProblemType);
    }

    [Fact]
    public async Task VerifyAccountRequest_Rejects_Jwk_Request()
    {
        var acme = await CreateServiceAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var url = "https://homeca.test/acme/order/x/finalize";
        var body = CreateSignedJws(key, acme.CreateNonce(), url, "{}", out _);

        var problem = await Assert.ThrowsAsync<AcmeProblemException>(() => acme.VerifyAccountRequestAsync(body, url, CancellationToken.None));

        Assert.Equal("malformed", problem.ProblemType);
    }

    [Fact]
    public async Task Order_Of_Another_Account_Is_Not_Visible_Or_Finalizable()
    {
        var acme = await CreateServiceAsync();
        using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owner = await acme.NewAccountAsync(CreateJwk(ownerKey), null, CancellationToken.None);
        var other = await acme.NewAccountAsync(CreateJwk(otherKey), null, CancellationToken.None);
        var order = await acme.NewOrderAsync(owner.Id, [new Rfc8555Identifier("dns", "host.example.test")], CancellationToken.None);
        var authorization = Assert.Single(order.Authorizations);

        Assert.Null(await acme.GetOrderAsync(order.Id, CancellationToken.None, other.Id));
        Assert.Null(await acme.GetAuthorizationAsync(authorization.Id, other.Id, CancellationToken.None));
        Assert.Null(await acme.GetChallengeAsync(authorization.Challenges[0].Id, other.Id, CancellationToken.None));
        var problem = await Assert.ThrowsAsync<AcmeProblemException>(() => acme.FinalizeOrderAsync(order.Id, other.Id, [], CancellationToken.None));
        Assert.Equal("Order not found.", problem.Message);
    }

    [Theory]
    [InlineData("00DCFFF0DA4113AD3924F871E4BBAB3955", "dcfff0da4113ad3924f871e4bbab3955")]
    [InlineData("5CFFF0DA4113AD3924F871E4BBAB3955", "5cfff0da4113ad3924f871e4bbab3955")]
    [InlineData("00FFF0DA4113AD3924F871E4BBAB39", "00fff0da4113ad3924f871e4bbab39")]
    public void CertificateId_Ignores_Der_Sign_Padding(string serialNumber, string expectedId) =>
        Assert.Equal(expectedId, Rfc8555AcmeService.CertificateIdFromSerialNumber(serialNumber));

    [Fact]
    public async Task Http01_Timeout_Marks_Challenge_Invalid_Instead_Of_Failing()
    {
        var hangingClient = new HttpClient(new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); }))
        {
            Timeout = TimeSpan.FromMilliseconds(100)
        };
        var acme = await CreateServiceAsync(hangingClient);
        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var account = await acme.NewAccountAsync(CreateJwk(accountKey), null, CancellationToken.None);
        var order = await acme.NewOrderAsync(account.Id, [new Rfc8555Identifier("dns", "host.example.test")], CancellationToken.None);

        var challenge = await acme.RespondToChallengeAsync(order.Authorizations[0].Challenges[0].Id, account.Id, CancellationToken.None);

        Assert.NotNull(challenge);
        Assert.Equal("invalid", challenge.Status);
    }

    [Fact]
    public async Task Finalize_Issues_Certificate_Bound_To_Its_Issuer_Crl()
    {
        var (acme, account, order) = await CreateReadyOrderAsync();
        var issuingId = (await _authorities!.GetDefaultIssuingAsync(CancellationToken.None)).Id;

        var finalized = await acme.FinalizeOrderAsync(order.Id, account.Id, CreateCsr(), CancellationToken.None);

        Assert.Equal("valid", finalized.Status);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_fixture.RootPath, "certificates", finalized.CertificateId!, "certificate.pfx"), null);
        var cdp = Encoding.ASCII.GetString(certificate.Extensions["2.5.29.31"]!.RawData);
        Assert.Contains($"/api/v1/crl/{issuingId}", cdp);
        var keyUsage = Assert.IsType<X509KeyUsageExtension>(certificate.Extensions["2.5.29.15"]);
        Assert.False(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment), "EC keys cannot be used for key encipherment.");
    }

    [Fact]
    public async Task Finalize_Rejects_Leaf_That_Would_Outlive_The_Issuing_Ca()
    {
        var (acme, account, order) = await CreateReadyOrderAsync();
        var root = (await _authorities!.ListAsync(CancellationToken.None)).Single(a => a.Type == "root");
        await _authorities.RotateIntermediateAsync(new RotateIntermediateRequest("Short CA", "CN=Short CA", root.Id, 100, "ECC", 7), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => acme.FinalizeOrderAsync(order.Id, account.Id, CreateCsr(), CancellationToken.None));

        Assert.Contains("rotate it", exception.Message);
        Assert.Equal("ready", (await acme.GetOrderAsync(order.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Finalize_Rejects_Csr_For_Other_Names()
    {
        var (acme, account, order) = await CreateReadyOrderAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=host.example.test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("host.example.test");
        san.AddDnsName("other.example.test");
        request.CertificateExtensions.Add(san.Build());

        var problem = await Assert.ThrowsAsync<AcmeProblemException>(() => acme.FinalizeOrderAsync(order.Id, account.Id, request.CreateSigningRequest(), CancellationToken.None));

        Assert.Equal("badCSR", problem.ProblemType);
        Assert.Equal("ready", (await acme.GetOrderAsync(order.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Finalize_Rejects_Garbage_Csr_As_BadCsr()
    {
        var (acme, account, order) = await CreateReadyOrderAsync();

        var problem = await Assert.ThrowsAsync<AcmeProblemException>(() => acme.FinalizeOrderAsync(order.Id, account.Id, [1, 2, 3], CancellationToken.None));

        Assert.Equal("badCSR", problem.ProblemType);
    }

    [Fact]
    public async Task Concurrent_Finalize_Issues_Exactly_One_Certificate()
    {
        var (acme, account, order) = await CreateReadyOrderAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try { return (await acme.FinalizeOrderAsync(order.Id, account.Id, CreateCsr(), CancellationToken.None)).CertificateId; }
            catch (AcmeProblemException problem) when (problem.ProblemType == "orderNotReady") { return null; }
        })));

        var final = await acme.GetOrderAsync(order.Id, CancellationToken.None);
        Assert.Equal("valid", final!.Status);
        // Every successful call returns the same certificate, and only that one was issued.
        Assert.All(attempts.Where(id => id is not null), id => Assert.Equal(final.CertificateId, id));
        Assert.Single(Directory.GetDirectories(Path.Combine(_fixture.RootPath, "certificates")));
    }

    private async Task<(Rfc8555AcmeService Acme, Rfc8555Account Account, Rfc8555Order Order)> CreateReadyOrderAsync()
    {
        Rfc8555Account? account = null;
        // Answers HTTP-01 with the correct key authorization for the requested token.
        var http01 = new HttpClient(new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($"{request.RequestUri!.Segments[^1]}.{account!.Thumbprint}")
        })));
        var acme = await CreateServiceAsync(http01);
        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        account = await acme.NewAccountAsync(CreateJwk(accountKey), null, CancellationToken.None);
        var order = await acme.NewOrderAsync(account.Id, [new Rfc8555Identifier("dns", "host.example.test")], CancellationToken.None);
        var challenge = await acme.RespondToChallengeAsync(order.Authorizations[0].Challenges[0].Id, account.Id, CancellationToken.None);
        Assert.Equal("valid", challenge!.Status);
        return (acme, account, order);
    }

    private static byte[] CreateCsr()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=host.example.test", key, HashAlgorithmName.SHA256).CreateSigningRequest();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private CertificateAuthorityService? _authorities;

    private async Task<Rfc8555AcmeService> CreateServiceAsync(HttpClient? http01Client = null)
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        _authorities = authorities;
        var profiles = new TargetProfileRegistry(storage);
        var deployments = new DeploymentPackageService(profiles, NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        var domains = new DomainRegistry(storage);
        await domains.AddAsync(new CreateDomainRequest("example.test", true, null), CancellationToken.None);
        return new Rfc8555AcmeService(authorities, certificates, new AcmeIpSanPolicyRegistry(storage), domains, storage, _fixture.CreateOptions(), NullLogger<Rfc8555AcmeService>.Instance,
            http01Client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    }

    private static byte[] CreateSignedJws(ECDsa key, string nonce, string url, string payload, out JsonObject jwk)
    {
        jwk = CreateJwk(key);
        var protectedHeader = new JsonObject { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url, ["jwk"] = jwk };
        var protectedB64 = Rfc8555AcmeService.Base64UrlEncode(Encoding.UTF8.GetBytes(protectedHeader.ToJsonString()));
        var payloadB64 = Rfc8555AcmeService.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["protected"] = protectedB64,
            ["payload"] = payloadB64,
            ["signature"] = Rfc8555AcmeService.Base64UrlEncode(signature)
        });
    }

    private static byte[] CreateKidSignedJws(ECDsa key, string nonce, string url, string payload, string kid)
    {
        var protectedHeader = new JsonObject { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url, ["kid"] = kid };
        var protectedB64 = Rfc8555AcmeService.Base64UrlEncode(Encoding.UTF8.GetBytes(protectedHeader.ToJsonString()));
        var payloadB64 = Rfc8555AcmeService.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["protected"] = protectedB64,
            ["payload"] = payloadB64,
            ["signature"] = Rfc8555AcmeService.Base64UrlEncode(signature)
        });
    }

    private static JsonObject CreateJwk(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Rfc8555AcmeService.Base64UrlEncode(parameters.Q.X!),
            ["y"] = Rfc8555AcmeService.Base64UrlEncode(parameters.Q.Y!)
        };
    }

    public void Dispose() => _fixture.Dispose();
}
