using System.Net;
using HomeCA.Service.Acme;

namespace HomeCA.Tests;

public sealed class AcmeIpSanPolicyRegistryTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Enabled_Policy_Requires_At_Least_One_Network()
    {
        var policy = new AcmeIpSanPolicyRegistry(_fixture.CreateStorage());

        await Assert.ThrowsAsync<ArgumentException>(() => policy.UpdateAsync(new(true, []), CancellationToken.None));
    }

    [Fact]
    public async Task Policy_Normalizes_Networks_And_Restricts_Addresses()
    {
        var policy = new AcmeIpSanPolicyRegistry(_fixture.CreateStorage());

        var saved = await policy.UpdateAsync(new(true, ["192.168.20.1", "fd00::/8"]), CancellationToken.None);

        Assert.True(saved.Enabled);
        Assert.Contains("192.168.20.1/32", saved.AllowedNetworks);
        Assert.True(AcmeIpSanPolicyRegistry.IsAllowed(IPAddress.Parse("192.168.20.1"), saved.AllowedNetworks));
        Assert.False(AcmeIpSanPolicyRegistry.IsAllowed(IPAddress.Parse("192.168.21.1"), saved.AllowedNetworks));
    }

    public void Dispose() => _fixture.Dispose();
}
