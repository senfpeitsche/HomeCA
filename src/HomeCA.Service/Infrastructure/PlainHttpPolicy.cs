namespace HomeCA.Service.Infrastructure;

/// <summary>
/// Decides which requests may stay on plain HTTP once HomeCA serves HTTPS itself. CRLs and the trust
/// anchor must be reachable before a client trusts the HTTPS endpoint, existing ACME clients may still
/// use the HTTP directory, and health checks run locally. Everything else is redirected to HTTPS.
/// </summary>
public static class PlainHttpPolicy
{
    private static readonly PathString[] AllowedPrefixes =
    [
        "/health",
        "/api/v1/crl",
        "/api/v1/trust-anchor",
        "/acme",
        "/.well-known"
    ];

    public static bool IsAllowedOverPlainHttp(PathString path) =>
        AllowedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the HTTPS port when the process listens on both HTTP and HTTPS.</summary>
    public static bool TryGetHttpsPort(string? urls, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(urls)) return false;
        var entries = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!entries.Any(entry => entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return false;
        var https = entries.FirstOrDefault(entry => entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        // Kestrel accepts wildcard hosts that Uri cannot parse.
        if (https is null || !Uri.TryCreate(https.Replace("://*", "://localhost").Replace("://+", "://localhost"), UriKind.Absolute, out var uri)) return false;
        port = uri.Port;
        return true;
    }
}
