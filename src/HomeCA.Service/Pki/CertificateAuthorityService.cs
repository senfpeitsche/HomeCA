using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Pki;

public sealed class CertificateAuthorityService(HomeCaStorage storage, ILogger<CertificateAuthorityService> logger)
{
    private readonly string _authorityRoot = Path.Combine(storage.RootPath, "authorities");

    public async Task<AuthorityInventory> InitializeAsync(CancellationToken cancellationToken)
    {
        var root = EnsureRoot();
        var issuing = EnsureIssuing(root);
        await EnsureSshAuthorityAsync("ssh-host", "HomeCA SSH Host CA", cancellationToken);
        await EnsureSshAuthorityAsync("ssh-user", "HomeCA SSH User CA", cancellationToken);
        return new AuthorityInventory(root.Subject, root.NotAfter, issuing.Subject, issuing.NotAfter, "ssh-host", "ssh-user");
    }

    private X509Certificate2 EnsureRoot()
    {
        var path = Path.Combine(_authorityRoot, "root", "root-ca.pfx");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=HomeCA Root CA", key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(path, created.Export(X509ContentType.Pkcs12));
        logger.LogInformation("Created root certificate authority");
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    private X509Certificate2 EnsureIssuing(X509Certificate2 root)
    {
        var path = Path.Combine(_authorityRoot, "tls-issuing", "tls-issuing-ca.pfx");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=HomeCA TLS Issuing CA", key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        File.WriteAllBytes(path, withKey.Export(X509ContentType.Pkcs12));
        logger.LogInformation("Created TLS issuing certificate authority");
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    private async Task EnsureSshAuthorityAsync(string name, string comment, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_authorityRoot, name);
        var privateKey = Path.Combine(directory, "ca");
        if (File.Exists(privateKey)) return;
        Directory.CreateDirectory(directory);
        var startInfo = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("ed25519");
        startInfo.ArgumentList.Add("-N");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(privateKey);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(comment);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start ssh-keygen. Install openssh-client in the Debian LXC.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"ssh-keygen failed: {await process.StandardError.ReadToEndAsync(cancellationToken)}");
        logger.LogInformation("Created {AuthorityName} authority", name);
    }
}

public sealed record AuthorityInventory(string RootSubject, DateTime RootExpiresAt, string TlsIssuingSubject, DateTime TlsIssuingExpiresAt, string SshHostAuthority, string SshUserAuthority);
