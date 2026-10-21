using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Connects to a deployment target over TLS and reports whether it serves the current certificate. Any served
/// certificate is accepted for the comparison; the probe only reads it and sends nothing beyond the handshake.
/// </summary>
public sealed class DeploymentProbeService(HomeCaStorage storage, DeploymentTargetRegistry targets, ILogger<DeploymentProbeService> logger)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<DeploymentProbeResult?> ProbeAsync(string targetId, CancellationToken ct)
    {
        var target = await targets.GetAsync(targetId, ct);
        if (target is null) return null;
        var result = await ProbeAsync(target, ct);
        await targets.RecordProbeAsync(target.Id, result, ct);
        return result;
    }

    internal async Task<DeploymentProbeResult> ProbeAsync(DeploymentTarget target, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(target.Host))
            return new DeploymentProbeResult(now, DeploymentProbeResult.Unreachable, Error: "No host is configured for this target.");

        X509Certificate2? served;
        try
        {
            served = await FetchServedCertificateAsync(target.Host, target.Port, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Deployment target {TargetId} at {Host}:{Port} is unreachable: {Reason}", target.Id, target.Host, target.Port, exception.Message);
            return new DeploymentProbeResult(now, DeploymentProbeResult.Unreachable, Error: exception.Message);
        }

        using (served)
        {
            if (served is null) return new DeploymentProbeResult(now, DeploymentProbeResult.Unreachable, Error: "The host did not present a certificate.");
            var fingerprint = Fingerprint(served);
            var status = Classify(fingerprint, CertificateFingerprint(target.CertificateId), KnownFingerprints());
            return new DeploymentProbeResult(now, status, fingerprint, served.NotAfter.ToUniversalTime());
        }
    }

    internal static string Classify(string servedFingerprint, string? currentFingerprint, IReadOnlySet<string> knownFingerprints) =>
        string.Equals(servedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase) ? DeploymentProbeResult.Current
        : knownFingerprints.Contains(servedFingerprint) ? DeploymentProbeResult.Outdated
        : DeploymentProbeResult.Foreign;

    /// <summary>SHA-256 over the DER certificate, the same value the deployment manifest reports.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    public string? CertificateFingerprint(string certificateId)
    {
        var path = Path.Combine(storage.RootPath, "certificates", certificateId, "certificate.pfx");
        if (!File.Exists(path)) return null;
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
        return Fingerprint(certificate);
    }

    private HashSet<string> KnownFingerprints()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(storage.RootPath, "certificates");
        if (!Directory.Exists(root)) return known;
        foreach (var path in Directory.EnumerateFiles(root, "certificate.pfx", SearchOption.AllDirectories))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                known.Add(Fingerprint(certificate));
            }
            catch (CryptographicException exception)
            {
                logger.LogWarning(exception, "Skipped unreadable certificate {Path} while classifying a probe", path);
            }
        }
        return known;
    }

    private static async Task<X509Certificate2?> FetchServedCertificateAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeout.Token);
        if (port == RdpPort) await NegotiateRdpTlsAsync(client.GetStream(), timeout.Token);
        X509Certificate2? served = null;
        await using var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is not null) served = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return true;
        });
        // An IP address is not a valid SNI name; send none so the target picks its default certificate.
        var targetHost = System.Net.IPAddress.TryParse(host, out _) ? string.Empty : host;
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = targetHost }, timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException)
        {
            // The certificate is captured during the handshake even if it fails later; without one the port does not speak TLS.
            if (served is not null) return served;
            throw new IOException($"Port {port} answered, but not with TLS ({exception.Message}).", exception);
        }
        return served;
    }

    internal const int RdpPort = 3389;

    /// <summary>
    /// RDP starts TLS only after an X.224 connection request (MS-RDPBCGR 2.2.1.1), so the probe asks for TLS or
    /// CredSSP like a client would and returns once the server agreed; the TLS handshake follows on the same stream.
    /// </summary>
    internal static async Task NegotiateRdpTlsAsync(Stream stream, CancellationToken ct)
    {
        byte[] request =
        [
            0x03, 0x00, 0x00, 0x13,                         // TPKT, 19 bytes
            0x0e, 0xe0, 0x00, 0x00, 0x00, 0x00, 0x00,       // X.224 connection request
            0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00  // RDP_NEG_REQ: PROTOCOL_SSL | PROTOCOL_HYBRID
        ];
        await stream.WriteAsync(request, ct);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = (header[2] << 8) | header[3];
        if (header[0] != 0x03 || length < 11 || length > 64) throw new IOException($"Port {RdpPort} answered, but not with RDP.");
        var body = new byte[length - 4];
        await stream.ReadExactlyAsync(body, ct);
        // X.224 connection confirm, then RDP_NEG_RSP (2) with the selected protocol or RDP_NEG_FAILURE (3).
        if ((body[1] & 0xf0) != 0xd0) throw new IOException($"Port {RdpPort} answered, but not with RDP.");
        if (body.Length < 15 || body[7] != 0x02)
            throw new IOException($"The RDP server on port {RdpPort} does not offer TLS{(body.Length >= 15 && body[7] == 0x03 ? $" (failure code {body[11]})" : string.Empty)}.");
        if (body[11] is not (0x01 or 0x02 or 0x08)) throw new IOException($"The RDP server on port {RdpPort} selected a protocol without TLS.");
    }
}
