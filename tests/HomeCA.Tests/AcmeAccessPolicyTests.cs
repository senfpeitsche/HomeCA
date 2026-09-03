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

        await policy.AuthorizeNewAccountAsync(IPAddress.Parse("192.168.10.42"), null, AccountJwk(), "https://pki.example/acme/new-acct", CancellationToken.None);
    }

    [Fact]
    public async Task NonAllowlistedClient_RequiresEab()
    {
        var policy = new AcmeAccessPolicyRegistry(_fixture.CreateStorage());

        var exception = await Assert.ThrowsAsync<AcmeProblemException>(() => policy.AuthorizeNewAccountAsync(IPAddress.Parse("192.168.10.42"), null, AccountJwk(), "https://pki.example/acme/new-acct", CancellationToken.None));

        Assert.Equal("externalAccountRequired", exception.ProblemType);
    }

    [Fact]
    public async Task NonAllowlistedClient_WithValidEab_CanRegisterOnlyOneAccount()
    {
        var policy = new AcmeAccessPolicyRegistry(_fixture.CreateStorage());
        var credentials = await policy.CreateEabAsync(new CreateAcmeEabCredentialRequest("opnsense-fw"), CancellationToken.None);
        var jwk = AccountJwk();
        const string url = "https://pki.example/acme/new-acct";

        var keyId = await policy.AuthorizeNewAccountAsync(IPAddress.Parse("192.168.10.42"), CreateEabBinding(credentials, jwk, url), jwk, url, CancellationToken.None);
        Assert.Equal(credentials.KeyId, keyId);
        await policy.AssociateEabWithAccountAsync(keyId!, "acme-account-1", CancellationToken.None);
        var accessPolicy = await policy.GetAsync(CancellationToken.None);
        var credential = Assert.Single(accessPolicy.EabCredentials);
        Assert.Equal("opnsense-fw", credential.Name);
        Assert.Equal("acme-account-1", credential.AccountId);
        Assert.NotNull(credential.UsedAt);

        var exception = await Assert.ThrowsAsync<AcmeProblemException>(() => policy.AuthorizeNewAccountAsync(IPAddress.Parse("192.168.10.42"), CreateEabBinding(credentials, AccountJwk(), url), AccountJwk(), url, CancellationToken.None));
        Assert.Equal("unauthorized", exception.ProblemType);
    }

    [Fact]
    public async Task RevokedEab_CannotRegisterAnAccount()
    {
        var policy = new AcmeAccessPolicyRegistry(_fixture.CreateStorage());
        var credentials = await policy.CreateEabAsync(new CreateAcmeEabCredentialRequest("nas"), CancellationToken.None);
        await policy.RevokeEabAsync(credentials.KeyId, CancellationToken.None);
        var jwk = AccountJwk();

        var exception = await Assert.ThrowsAsync<AcmeProblemException>(() => policy.AuthorizeNewAccountAsync(IPAddress.Parse("192.168.10.42"), CreateEabBinding(credentials, jwk, "https://pki.example/acme/new-acct"), jwk, "https://pki.example/acme/new-acct", CancellationToken.None));
        Assert.Equal("unauthorized", exception.ProblemType);
    }

    [Fact]
    public async Task LegacyGlobalEab_IsMigratedToANamedCredential()
    {
        var storage = _fixture.CreateStorage();
        var stateDirectory = Path.Combine(_fixture.RootPath, "state");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, "acme-access-policy.json"), """{"AllowlistedClientNetworks":[],"EabKeyId":"legacy-kid","EabHmacKey":"legacy-secret"}""");

        var policy = new AcmeAccessPolicyRegistry(storage);
        var accessPolicy = await policy.GetAsync(CancellationToken.None);

        var credential = Assert.Single(accessPolicy.EabCredentials);
        Assert.Equal("legacy-kid", credential.KeyId);
        Assert.Equal("Legacy EAB credential", credential.Name);
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
