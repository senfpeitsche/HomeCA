using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeCA.Service.Security;

/// <summary>
/// Checks an issuance request against the scope of an issuance token. Names are normalized (IDN to ASCII, lower
/// case, trailing dot removed) before the suffix comparison, so case or encoding tricks cannot widen the scope.
/// </summary>
public static class IssuanceTokenScope
{
    private static readonly IdnMapping Idn = new();

    /// <summary>Normalized ASCII host name, or null when the value is not a DNS name.</summary>
    public static string? NormalizeDnsName(string? name)
    {
        var trimmed = name?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(trimmed)) return null;
        string ascii;
        try { ascii = Idn.GetAscii(trimmed).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
        return Uri.CheckHostName(ascii) == UriHostNameType.Dns ? ascii : null;
    }

    /// <summary>True when the normalized name equals the suffix or is a subdomain of it (separated by a dot).</summary>
    public static bool MatchesSuffix(string name, string suffix) =>
        name == suffix || name.EndsWith("." + suffix, StringComparison.Ordinal);

    /// <summary>
    /// Returns the first scope violation, or the normalized names when the request is within scope. A wildcard is
    /// allowed when the part after <c>*.</c> satisfies the suffix rule.
    /// </summary>
    public static ScopeCheck Check(IssuanceToken token, string? usage, IEnumerable<string>? dnsNames, IEnumerable<string>? ipAddresses, int validityDays)
    {
        if (!token.AllowedUsages.Any(allowed => allowed.Equals(usage, StringComparison.OrdinalIgnoreCase)))
            return ScopeCheck.Deny($"The usage '{usage}' is not allowed for this token.");
        if (validityDays > token.MaxValidityDays)
            return ScopeCheck.Deny($"The validity of {validityDays} days exceeds the token maximum of {token.MaxValidityDays} days.");

        var normalizedDns = new List<string>();
        foreach (var requested in dnsNames ?? [])
        {
            var isWildcard = requested.Trim().StartsWith("*.", StringComparison.Ordinal);
            var host = NormalizeDnsName(isWildcard ? requested.Trim()[2..] : requested);
            if (host is null || !token.AllowedDnsSuffixes.Any(suffix => MatchesSuffix(host, suffix)))
                return ScopeCheck.Deny($"The name '{requested}' is outside the token scope.");
            var normalized = isWildcard ? "*." + host : host;
            if (!normalizedDns.Contains(normalized)) normalizedDns.Add(normalized);
        }

        var normalizedIps = new List<string>();
        foreach (var requested in ipAddresses ?? [])
        {
            if (!IPAddress.TryParse(requested?.Trim(), out var address)
                || !token.AllowedIpCidrs.Any(cidr => IPNetwork.TryParse(cidr, out var network) && network.Contains(address)))
                return ScopeCheck.Deny($"The IP address '{requested}' is outside the token scope.");
            if (!normalizedIps.Contains(address.ToString())) normalizedIps.Add(address.ToString());
        }

        return new ScopeCheck(null, normalizedDns, normalizedIps);
    }

    /// <summary>Subject CN and all DNS and IP SANs of a CSR. The CN is always included, so it cannot carry an extra name.</summary>
    public static (IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses) CsrNames(string csrPem)
    {
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new ArgumentException($"The CSR is invalid or its signature does not verify: {exception.Message}");
        }

        var dnsNames = new List<string>();
        var ipAddresses = new List<string>();
        foreach (var extension in csr.CertificateExtensions.Where(extension => extension.Oid?.Value == "2.5.29.17"))
        {
            var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
            dnsNames.AddRange(san.EnumerateDnsNames());
            ipAddresses.AddRange(san.EnumerateIPAddresses().Select(address => address.ToString()));
        }
        foreach (var commonName in csr.SubjectName.EnumerateRelativeDistinguishedNames()
                     .Where(name => name.GetSingleElementType().Value == "2.5.4.3")
                     .Select(name => name.GetSingleElementValue())
                     .Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (IPAddress.TryParse(commonName, out var address)) ipAddresses.Add(address.ToString());
            else dnsNames.Add(commonName!);
        }
        return (dnsNames, ipAddresses);
    }
}

public sealed record ScopeCheck(string? Violation, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses)
{
    public bool Allowed => Violation is null;
    public static ScopeCheck Deny(string violation) => new(violation, [], []);
}
