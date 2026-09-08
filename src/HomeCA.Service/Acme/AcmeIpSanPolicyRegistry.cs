using System.Net;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Acme;

/// <summary>Controls optional IP SAN discovery for RFC 8555 DNS orders.</summary>
public sealed class AcmeIpSanPolicyRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "acme-ip-san-policy.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AcmeIpSanPolicy> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var policy = await ReadUnsafeAsync(ct) ?? new(false, []);
            if (!File.Exists(_path)) await WriteUnsafeAsync(policy, ct);
            return policy;
        }
        finally { _gate.Release(); }
    }

    public async Task<AcmeIpSanPolicy> UpdateAsync(UpdateAcmeIpSanPolicyRequest request, CancellationToken ct)
    {
        var networks = NormalizeNetworks(request.AllowedNetworks);
        if (request.Enabled && networks.Length == 0)
            throw new ArgumentException("Add at least one allowed network before enabling automatic ACME IP SANs.");
        var policy = new AcmeIpSanPolicy(request.Enabled, networks);
        await _gate.WaitAsync(ct);
        try { await WriteUnsafeAsync(policy, ct); return policy; }
        finally { _gate.Release(); }
    }

    public static bool IsAllowed(IPAddress address, IReadOnlyList<string> networks)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(network => IPNetwork.TryParse(network, out var parsed) && parsed.Contains(address));
    }

    private static string[] NormalizeNetworks(IReadOnlyList<string>? networks) => (networks ?? [])
        .Select(value => value.Trim()).Where(value => value.Length > 0).Select(value =>
        {
            if (IPAddress.TryParse(value, out var address))
                return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128).ToString();
            if (!IPNetwork.TryParse(value, out var network))
                throw new ArgumentException("Every allowed IP SAN network must be an IP address or CIDR, for example 192.168.10.0/24.");
            return network.ToString();
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private async Task<AcmeIpSanPolicy?> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<AcmeIpSanPolicy>(stream, cancellationToken: ct);
    }

    private async Task WriteUnsafeAsync(AcmeIpSanPolicy policy, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, policy, cancellationToken: ct);
        File.Move(temporary, _path, true);
    }
}

public sealed record UpdateAcmeIpSanPolicyRequest(bool Enabled, IReadOnlyList<string>? AllowedNetworks);
public sealed record AcmeIpSanPolicy(bool Enabled, IReadOnlyList<string> AllowedNetworks);
