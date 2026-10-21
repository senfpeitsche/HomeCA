using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace HomeCA.Service.Pki;

/// <summary>
/// Builds, reads and enforces the X.509 Name Constraints extension (RFC 5280, 4.2.1.10) for permitted DNS
/// domains and IP networks. A CA limited this way cannot vouch for names outside the homelab, which makes
/// installing its root on everyday devices far less risky.
/// </summary>
public static class NameConstraints
{
    public const string Oid = "2.5.29.30";

    public sealed record Permitted(IReadOnlyList<string> DnsDomains, IReadOnlyList<string> IpNetworks)
    {
        public bool IsEmpty => DnsDomains.Count == 0 && IpNetworks.Count == 0;
    }

    /// <summary>Validates and normalizes user input: lower-case domains without a leading dot, networks in CIDR form.</summary>
    public static Permitted Normalize(IEnumerable<string>? dnsDomains, IEnumerable<string>? ipNetworks)
    {
        var domains = (dnsDomains ?? [])
            .Select(domain => domain.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .Distinct()
            .ToList();
        foreach (var domain in domains)
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains('*'))
                throw new ArgumentException($"'{domain}' is not a valid DNS domain for a name constraint.");

        var networks = (ipNetworks ?? [])
            .Select(network => network.Trim())
            .Where(network => network.Length > 0)
            .Select(network => IPNetwork.TryParse(network, out var parsed) ? parsed.ToString() : throw new ArgumentException($"'{network}' is not a network in CIDR notation, such as 192.168.0.0/16."))
            .Distinct()
            .ToList();
        return new Permitted(domains, networks);
    }

    public static X509Extension Build(Permitted permitted)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) // permittedSubtrees [0]
        {
            foreach (var domain in permitted.DnsDomains)
                using (writer.PushSequence()) // GeneralSubtree
                    writer.WriteCharacterString(UniversalTagNumber.IA5String, domain, new Asn1Tag(TagClass.ContextSpecific, 2)); // dNSName [2]
            foreach (var network in permitted.IpNetworks.Select(IPNetwork.Parse))
                using (writer.PushSequence())
                    writer.WriteOctetString(AddressAndMask(network), new Asn1Tag(TagClass.ContextSpecific, 7)); // iPAddress [7]
        }
        // Critical, as RFC 5280 requires, so clients that cannot evaluate it reject the CA instead of ignoring the limit.
        return new X509Extension(Oid, writer.Encode(), critical: true);
    }

    /// <summary>Reads the permitted DNS domains and IP networks of a CA certificate; null when it has no constraints.</summary>
    public static Permitted? Read(X509Certificate2 certificate)
    {
        if (certificate.Extensions[Oid] is not { } extension) return null;
        var domains = new List<string>();
        var networks = new List<string>();
        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        if (!reader.HasData || !reader.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) return new Permitted(domains, networks);
        var subtrees = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        while (subtrees.HasData)
        {
            var subtree = subtrees.ReadSequence();
            var tag = subtree.PeekTag();
            if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                domains.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)).TrimStart('.').ToLowerInvariant());
            else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                networks.Add(FromAddressAndMask(subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7))));
        }
        return new Permitted(domains, networks);
    }

    /// <summary>Throws when a SAN falls outside the issuer's permitted names; such a certificate would be rejected by clients.</summary>
    public static void EnsurePermitted(X509Certificate2 issuer, IEnumerable<string> dnsNames, IEnumerable<string> ipAddresses)
    {
        if (Read(issuer) is not { } permitted) return;
        foreach (var name in dnsNames)
            if (permitted.DnsDomains.Count > 0 && !IsDnsPermitted(name, permitted.DnsDomains))
                throw new ArgumentException($"'{name}' is outside the DNS domains the issuing CA may certify ({string.Join(", ", permitted.DnsDomains)}).");
        var networks = permitted.IpNetworks.Select(IPNetwork.Parse).ToList();
        foreach (var address in ipAddresses)
        {
            if (!IPAddress.TryParse(address, out var parsed)) continue;
            // An address family without a permitted network is unconstrained (RFC 5280), so only matching families count.
            var sameFamily = networks.Where(network => network.BaseAddress.AddressFamily == parsed.AddressFamily).ToList();
            if (sameFamily.Count > 0 && !sameFamily.Any(network => network.Contains(parsed)))
                throw new ArgumentException($"'{address}' is outside the IP networks the issuing CA may certify ({string.Join(", ", permitted.IpNetworks)}).");
        }
    }

    internal static bool IsDnsPermitted(string name, IReadOnlyList<string> domains)
    {
        var host = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("*.", StringComparison.Ordinal)) host = host[2..];
        return domains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private static byte[] AddressAndMask(IPNetwork network)
    {
        var address = network.BaseAddress.GetAddressBytes();
        var mask = new byte[address.Length];
        for (var bit = 0; bit < network.PrefixLength; bit++) mask[bit / 8] |= (byte)(0x80 >> (bit % 8));
        return [.. address, .. mask];
    }

    private static string FromAddressAndMask(byte[] value)
    {
        var length = value.Length / 2;
        var address = new IPAddress(value.AsSpan(0, length));
        var prefix = value.AsSpan(length).ToArray().Sum(octet => System.Numerics.BitOperations.PopCount(octet));
        return $"{address}/{prefix}";
    }

    /// <summary>Private and link-local ranges, offered as a starting point for the IP constraint.</summary>
    public static readonly IReadOnlyList<string> PrivateNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fd00::/8"];
}
