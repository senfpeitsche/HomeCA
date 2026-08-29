using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Operations;
public sealed class CertificateExpiryService(HomeCaStorage storage)
{
    private readonly string _root = Path.Combine(storage.RootPath, "certificates");
    public IReadOnlyList<CertificateExpiryWarning> GetWarnings(int days = 30) => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "certificate.pfx", SearchOption.AllDirectories).Select(path => X509CertificateLoader.LoadPkcs12FromFile(path, null)).Where(certificate => certificate.NotAfter <= DateTime.Now.AddDays(days)).Select(certificate => new CertificateExpiryWarning(certificate.Subject, certificate.NotAfter, (certificate.NotAfter-DateTime.Now).Days)).ToList() : [];
}
public sealed record CertificateExpiryWarning(string Subject, DateTime ExpiresAt, int DaysRemaining);
