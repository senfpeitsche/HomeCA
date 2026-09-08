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

    private async Task<Rfc8555AcmeService> CreateServiceAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var profiles = new TargetProfileRegistry(storage);
        var deployments = new DeploymentPackageService(profiles, NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        var domains = new DomainRegistry(storage);
        await domains.AddAsync(new CreateDomainRequest("example.test", true, null), CancellationToken.None);
        return new Rfc8555AcmeService(authorities, certificates, new AcmeIpSanPolicyRegistry(storage), domains, storage, _fixture.CreateOptions(), NullLogger<Rfc8555AcmeService>.Instance);
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
