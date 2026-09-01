using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;

namespace HomeCA.Service.Automation;

/// <summary>
/// Periodically checks renewal plans and re-issues certificates that are approaching expiration.
/// Runs every hour. When a certificate is within the plan's renewal window, a new certificate
/// is issued with the same SANs and key algorithm, and the plan is updated to reference the new certificate.
/// </summary>
public sealed class RenewalBackgroundService(
    IServiceProvider services,
    ILogger<RenewalBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Renewal background service started, checking every {Interval}", Interval);

        // Wait a bit on startup to let the app fully initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRenewAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Renewal check failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task CheckAndRenewAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var plans = scope.ServiceProvider.GetRequiredService<RenewalPlanRegistry>();
        var certificates = scope.ServiceProvider.GetRequiredService<CertificateIssuanceService>();
        var storage = scope.ServiceProvider.GetRequiredService<HomeCaStorage>();
        var notifications = scope.ServiceProvider.GetRequiredService<RenewalMailNotificationService>();

        var allPlans = await plans.ListAsync(cancellationToken);
        var enabledPlans = allPlans.Where(plan => plan.Enabled).ToList();

        if (enabledPlans.Count == 0) return;

        var allCertificates = await certificates.ListAsync(cancellationToken);
        var certificateMap = allCertificates.ToDictionary(cert => cert.Id, StringComparer.OrdinalIgnoreCase);

        var renewed = 0;
        foreach (var plan in enabledPlans)
        {
            try
            {
                if (!certificateMap.TryGetValue(plan.CertificateId, out var certificate)) continue;

                var daysRemaining = (certificate.ExpiresAt - DateTime.UtcNow).TotalDays;
                if (daysRemaining > plan.RenewBeforeDays) continue;

                logger.LogInformation("Certificate {CertificateId} ({Subject}) expires in {Days:F0} days, renewing (plan {PlanId})",
                    plan.CertificateId, certificate.Subject, daysRemaining, plan.Id);

                var request = BuildRenewalRequest(storage, plan.CertificateId);
                if (request is null)
                {
                    logger.LogWarning("Could not read original certificate {CertificateId} for renewal", plan.CertificateId);
                    continue;
                }

                var result = await certificates.IssueAsync(request, cancellationToken);

                // Update the plan to point to the new certificate
                await plans.UpdateAsync(plan.Id, new UpdateRenewalPlanRequest(plan.RenewBeforeDays, plan.Enabled, result.Id), cancellationToken);

                logger.LogInformation("Renewed certificate {OldId} → {NewId} ({Subject}), valid until {ExpiresAt:yyyy-MM-dd}",
                    plan.CertificateId, result.Id, result.Subject, result.ExpiresAt);
                try
                {
                    await notifications.SendRenewedAsync(result.Subject, result.ExpiresAt, cancellationToken);
                }
                catch (Exception notificationException) when (notificationException is not OperationCanceledException)
                {
                    // A mail-delivery failure must not make an already completed certificate renewal fail.
                    logger.LogError(notificationException, "Could not send renewal success notification for certificate {CertificateId}", result.Id);
                }
                renewed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to renew certificate {CertificateId} (plan {PlanId})", plan.CertificateId, plan.Id);
                try
                {
                    var subject = certificateMap.TryGetValue(plan.CertificateId, out var certificate) ? certificate.Subject : plan.CertificateId;
                    await notifications.SendFailureAsync(subject, exception, cancellationToken);
                }
                catch (Exception notificationException) when (notificationException is not OperationCanceledException)
                {
                    logger.LogError(notificationException, "Could not send renewal failure notification for plan {PlanId}", plan.Id);
                }
            }
        }

        if (renewed > 0)
        {
            logger.LogInformation("Renewal check complete: {Renewed} certificate(s) renewed", renewed);
        }
    }

    /// <summary>Reads the existing certificate's SANs and key algorithm to build a matching issuance request.</summary>
    private static IssueCertificateRequest? BuildRenewalRequest(HomeCaStorage storage, string certificateId)
    {
        var pfxPath = Path.Combine(storage.RootPath, "certificates", certificateId, "certificate.pfx");
        if (!File.Exists(pfxPath)) return null;

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);

        var dnsNames = new List<string>();
        var ipAddresses = new List<string>();

        // Extract SANs from the Subject Alternative Name extension
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17") continue; // Subject Alternative Name
            var sanExtension = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
            foreach (var name in sanExtension.EnumerateDnsNames()) dnsNames.Add(name);
            foreach (var ip in sanExtension.EnumerateIPAddresses()) ipAddresses.Add(ip.ToString());
        }

        if (dnsNames.Count == 0 && ipAddresses.Count == 0) return null;

        // Determine key algorithm from existing certificate
        var keyAlgorithm = certificate.PublicKey.Oid?.Value switch
        {
            "1.2.840.10045.2.1" => "ECC", // EC
            "1.2.840.113549.1.1.1" => "RSA", // RSA
            _ => "ECC"
        };

        // Determine RSA key size if applicable
        var rsaKeySize = 2048;
        if (keyAlgorithm == "RSA")
        {
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null) rsaKeySize = rsa.KeySize;
        }

        // Determine usage from EKU
        var usage = "TLS";
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku)
            {
                var hasClient = eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.2");
                if (hasClient) usage = "mTLS";
                break;
            }
        }

        // Use the same validity as the original certificate
        var validityDays = Math.Min(730, Math.Max(1, (int)(certificate.NotAfter - certificate.NotBefore).TotalDays));

        return new IssueCertificateRequest(usage, dnsNames, ipAddresses, validityDays, keyAlgorithm, rsaKeySize);
    }
}
