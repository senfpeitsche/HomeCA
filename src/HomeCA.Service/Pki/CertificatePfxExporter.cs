using System.Security.Cryptography.X509Certificates;

namespace HomeCA.Service.Pki;

/// <summary>Creates deployment PFX files with the leaf certificate and issuing CA.</summary>
public static class CertificatePfxExporter
{
    public static X509Certificate2 LoadCertificateWithExportablePrivateKey(string pfxPath)
    {
        var certificates = LoadExportableCollection(pfxPath, password: null);
        return certificates.FirstOrDefault(certificate => certificate.HasPrivateKey)
            ?? throw new InvalidOperationException("The PFX does not contain a certificate with a private key.");
    }

    public static X509Certificate2Collection LoadExportableCollection(string pfxPath, string? password)
    {
        var certificates = new X509Certificate2Collection();
#pragma warning disable SYSLIB0057 // The modern loader does not support exportable private-key flags.
        certificates.Import(pfxPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        return certificates;
    }

    public static byte[] ExportWithIssuingCertificate(X509Certificate2 certificate, string chainPath, string password)
    {
        var certificates = new X509Certificate2Collection { certificate };
        if (File.Exists(chainPath))
        {
            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(chainPath);
            var issuingCertificate = chain.FirstOrDefault(candidate =>
                string.Equals(candidate.Subject, certificate.Issuer, StringComparison.OrdinalIgnoreCase));
            if (issuingCertificate is not null) certificates.Add(issuingCertificate);
        }

        return certificates.Export(X509ContentType.Pkcs12, password)
            ?? throw new InvalidOperationException("PFX export did not produce any data.");
    }
}
