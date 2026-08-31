using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Operations;

public sealed class CertificateExpiryService(HomeCaStorage storage, ILogger<CertificateExpiryService> logger)
{
    private readonly string _root = Path.Combine(storage.RootPath, "certificates");

    public IReadOnlyList<CertificateExpiryWarning> GetWarnings(int days = 30)
    {
        if (!Directory.Exists(_root)) return [];

        var warnings = new List<CertificateExpiryWarning>();
        var cutoff = DateTime.Now.AddDays(days);

        foreach (var path in Directory.EnumerateFiles(_root, "certificate.pfx", SearchOption.AllDirectories))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
                if (certificate.NotAfter <= cutoff)
                {
                    warnings.Add(new CertificateExpiryWarning(
                        certificate.Subject,
                        certificate.NotAfter,
                        (certificate.NotAfter - DateTime.Now).Days));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load certificate from {Path}, skipping", path);
            }
        }

        return warnings;
    }
}

public sealed record CertificateExpiryWarning(string Subject, DateTime ExpiresAt, int DaysRemaining);
