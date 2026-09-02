using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HomeCA.Service.Acme;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class AcmeAccessPolicyTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task AllowlistedClient_CanRegisterWithoutEab()
    {
        var policy = new AcmeAccessPolicyRegistry(_fixture.CreateStorage());
        await policy.UpdateAsync(new UpdateAcmeAccessPolicyRequest(["192.168.10.42"]), CancellationToken.None);

        await policy.ValidateNewAccountAsync(IPAddress.Parse("192.168.10.42"), null, AccountJwk(), "https://pki.example/acme/new-acct", CancellationToken.None);
    }

    [Fact]
    public async Task NonAllowlistedClient_RequiresEab()
    {
        var policy = new AcmeAccessPolicyRegistry(_fixture.CreateStorage());

        var exception = await Assert.ThrowsAsync<AcmeProblemException>(() => policy.ValidateNewAccountAsync(IPAddress.Parse("192.168.10.42"), null, AccountJwk(), "https://pki.example/acme/new-acct", CancellationToken.None));

        Assert.Equal("externalAccountRequired", exception.ProblemType);
    }

    [Fact]
    public async Task NonAllowlistedClient_WithValidEab_CanRegister()
    {
        var policy = new AcmeAccessPolicyRegistry(_fixture.CreateStorage());
        var credentials = await policy.RotateEabAsync(CancellationToken.None);
        var jwk = AccountJwk();
        const string url = "https://pki.example/acme/new-acct";

        await policy.ValidateNewAccountAsync(IPAddress.Parse("192.168.10.42"), CreateEabBinding(credentials, jwk, url), jwk, url, CancellationToken.None);
    }

    private static JsonObject AccountJwk() => new()
    {
        ["kty"] = "EC", ["crv"] = "P-256", ["x"] = "example-x", ["y"] = "example-y"
    };

    private static JsonObject CreateEabBinding(AcmeEabCredentials credentials, JsonObject jwk, string url)
    {
        var header = Rfc8555AcmeService.Base64UrlEncode(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "HS256", ["kid"] = credentials.KeyId, ["url"] = url }.ToJsonString()));
        var payload = Rfc8555AcmeService.Base64UrlEncode(Encoding.UTF8.GetBytes(jwk.ToJsonString()));
        using var hmac = new HMACSHA256(Rfc8555AcmeService.Base64UrlDecode(credentials.HmacKey));
        var signature = Rfc8555AcmeService.Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return new JsonObject { ["protected"] = header, ["payload"] = payload, ["signature"] = signature };
    }

    public void Dispose() => _fixture.Dispose();
}
