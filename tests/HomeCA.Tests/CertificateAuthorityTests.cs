using HomeCA.Service.Pki;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class CertificateAuthorityTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Initialize_Creates_Root_And_Issuing_CA()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);

        var result = await service.InitializeAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("Root CA", result.RootSubject);
        Assert.Contains("Issuing CA", result.TlsIssuingSubject);
    }

    [Fact]
    public async Task Initialize_Twice_Throws()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);

        await service.InitializeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task List_Returns_Created_Authorities()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await service.InitializeAsync(CancellationToken.None);

        var list = await service.ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.Type == "root");
        Assert.Contains(list, a => a.Type == "intermediate");
    }

    [Fact]
    public async Task Create_Custom_Root_CA()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);

        var root = await service.CreateAsync(new CreateAuthorityRequest("Test Root", "CN=Test Root CA", "root", null, 365, "ECC", 7), CancellationToken.None);

        Assert.Equal("Test Root", root.Name);
        Assert.Equal("root", root.Type);
        Assert.True(root.IsActive);
        Assert.False(root.IsRevoked);
    }

    [Fact]
    public async Task Create_Intermediate_Under_Root()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        var root = await service.CreateAsync(new CreateAuthorityRequest("Root", "CN=Root", "root", null, 365, "ECC", 7), CancellationToken.None);

        var issuing = await service.CreateAsync(new CreateAuthorityRequest("Issuing", "CN=Issuing", "intermediate", root.Id, 180, "ECC", 7), CancellationToken.None);

        Assert.Equal("intermediate", issuing.Type);
        Assert.Equal(root.Id, issuing.ParentId);
    }

    [Fact]
    public async Task Revoke_Authority()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        var root = await service.CreateAsync(new CreateAuthorityRequest("Root", "CN=Root", "root", null, 365, "ECC", 7), CancellationToken.None);

        var revoked = await service.RevokeAsync(root.Id, CancellationToken.None);

        Assert.NotNull(revoked);
        Assert.True(revoked.IsRevoked);
        Assert.False(revoked.IsActive);
    }

    [Fact]
    public async Task Export_Root_Certificate_Pem()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        var root = await service.CreateAsync(new CreateAuthorityRequest("Root", "CN=Root", "root", null, 365, "ECC", 7), CancellationToken.None);

        var export = await service.ExportCertificateAsync(root.Id, "pem", CancellationToken.None);

        Assert.NotNull(export);
        Assert.Contains("BEGIN CERTIFICATE", System.Text.Encoding.UTF8.GetString(export.Content));
    }

    [Fact]
    public async Task TrustAnchor_Returns_Root_Info()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await service.InitializeAsync(CancellationToken.None);

        var info = await service.GetTrustAnchorInfoAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.NotEmpty(info.Sha256Fingerprint);
        Assert.Contains("Root", info.Subject);
    }

    [Fact]
    public async Task TrustIntermediate_Returns_Active_Issuing_Certificate()
    {
        var storage = _fixture.CreateStorage();
        var service = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await service.InitializeAsync(CancellationToken.None);

        var export = await service.GetTrustIntermediateAsync("pem", CancellationToken.None);

        Assert.NotNull(export);
        Assert.Equal("homeca-issuing-ca.pem", export.FileName);
        Assert.Contains("BEGIN CERTIFICATE", System.Text.Encoding.UTF8.GetString(export.Content));
    }

    public void Dispose() => _fixture.Dispose();
}
