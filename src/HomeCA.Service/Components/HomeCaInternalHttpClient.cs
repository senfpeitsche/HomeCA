using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HomeCA.Service.Infrastructure;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Components;

/// <summary>
/// Creates HTTP clients for server-side UI calls back into this HomeCA instance.
/// When TLS is enabled, the certificate issued by HomeCA is accepted only when
/// it matches the certificate configured for this local service.
/// </summary>
public sealed class HomeCaInternalHttpClient(IOptions<HomeCaStorageOptions> options)
{
    private readonly string _tlsConfigPath = HomeCaStorageOptions.GetConfigurationFilePath(options.Value.ConfigurationPath, "tls.json");

    public HttpClient Create(Uri baseAddress)
    {
        var handler = new HttpClientHandler();
        if (baseAddress.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || MatchesConfiguredTlsCertificate(certificate);

        return new HttpClient(handler) { BaseAddress = baseAddress };
    }

    private bool MatchesConfiguredTlsCertificate(X509Certificate2? certificate)
    {
        if (certificate is null || !File.Exists(_tlsConfigPath)) return false;

        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(_tlsConfigPath));
            if (!config.RootElement.TryGetProperty("pfxPath", out var pfxPathElement)) return false;
            var pfxPath = pfxPathElement.GetString();
            if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath)) return false;

            using var configuredCertificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
            return string.Equals(
                configuredCertificate.Thumbprint,
                certificate.Thumbprint,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
