using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HomeCA.Service.Deployments;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Notifications;
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
    internal static readonly TimeSpan FailureNotificationInterval = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFailureNotification = new();

    /// <summary>Allows one failure notification per plan and interval; a successful renewal resets it.</summary>
    internal bool ShouldSendFailureNotification(string planId, DateTimeOffset now)
    {
        if (_lastFailureNotification.TryGetValue(planId, out var last) && now - last < FailureNotificationInterval) return false;
        _lastFailureNotification[planId] = now;
        return true;
    }

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
        var renewals = scope.ServiceProvider.GetRequiredService<CertificateRenewalService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var crl = scope.ServiceProvider.GetRequiredService<CrlService>();

        // Certificate renewal must still run when CRL maintenance fails as a whole.
        try
        {
            var renewedCrls = await crl.RenewExpiringAsync(cancellationToken);
            if (renewedCrls > 0)
                logger.LogInformation("Renewed {Count} missing or expiring CRL(s)", renewedCrls);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "CRL renewal failed; continuing with certificate renewals");
        }

        var enabledPlans = (await plans.ListAsync(cancellationToken)).Where(plan => plan.Enabled).ToList();
        if (enabledPlans.Count == 0) return;

        var certificateMap = (await certificates.ListAsync(cancellationToken, take: 500)).ToDictionary(cert => cert.Id, StringComparer.OrdinalIgnoreCase);

        var renewed = 0;
        // Two plans for the same certificate must not renew it twice; the first renewal moves both.
        foreach (var plan in enabledPlans.DistinctBy(plan => plan.CertificateId, StringComparer.OrdinalIgnoreCase))
        {
            if (!certificateMap.TryGetValue(plan.CertificateId, out var certificate)) continue;
            var daysRemaining = (certificate.ExpiresAt - DateTime.UtcNow).TotalDays;
            if (daysRemaining > plan.RenewBeforeDays) continue;

            logger.LogInformation("Certificate {CertificateId} ({Subject}) expires in {Days:F0} days, renewing (plan {PlanId})",
                plan.CertificateId, certificate.Subject, daysRemaining, plan.Id);
            try
            {
                var result = await renewals.RenewAsync(plan.CertificateId, cancellationToken);
                _lastFailureNotification.TryRemove(plan.Id, out _);
                // Delivery failures are logged by the notification service and never undo the renewal.
                await notifications.SendAsync(NotificationEvent.CertificateRenewed, result.Subject, result.ExpiresAt.ToString("yyyy-MM-dd"), cancellationToken);
                renewed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to renew certificate {CertificateId} (plan {PlanId})", plan.CertificateId, plan.Id);
                await plans.RecordFailureAsync(plan.Id, exception.Message, cancellationToken);
                // The check runs hourly; a persistent failure would otherwise send a notification every hour.
                if (!ShouldSendFailureNotification(plan.Id, DateTimeOffset.UtcNow))
                {
                    logger.LogDebug("Failure notification for plan {PlanId} suppressed; one was sent within the last {Interval}", plan.Id, FailureNotificationInterval);
                    continue;
                }
                await notifications.SendAsync(NotificationEvent.RenewalFailed, certificate.Subject, exception.Message, cancellationToken);
            }
        }

        if (renewed > 0)
        {
            logger.LogInformation("Renewal check complete: {Renewed} certificate(s) renewed", renewed);
        }
    }

    /// <summary>Reads the existing certificate's SANs and key algorithm to build a matching issuance request.</summary>
    internal static IssueCertificateRequest? BuildRenewalRequest(HomeCaStorage storage, string certificateId)
    {
        var pfxPath = Path.Combine(storage.RootPath, "certificates", certificateId, "certificate.pfx");
        if (!File.Exists(pfxPath)) return null;

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
        // Certificates signed from a CSR or obtained via ACME have their key elsewhere; a renewal with a
        // fresh HomeCA key would not reach the device that holds the original key.
        if (!certificate.HasPrivateKey) return null;

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
        // NotBefore/NotAfter are local times; across a DST change their difference is off by an hour,
        // which truncation would turn into one lost day per renewal. Issuance also backdates by 5 minutes.
        var validity = certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime();
        var validityDays = Math.Min(730, Math.Max(1, (int)Math.Round(validity.TotalDays)));

        return new IssueCertificateRequest(usage, dnsNames, ipAddresses, validityDays, keyAlgorithm, rsaKeySize, ReadTargetProfileId(storage, certificateId));
    }

    /// <summary>Keeps the deployment profile of the original certificate instead of falling back to generic TLS.</summary>
    private static string? ReadTargetProfileId(HomeCaStorage storage, string certificateId)
    {
        var snapshotPath = Path.Combine(storage.RootPath, "exports", certificateId, "profile-snapshot.json");
        if (!File.Exists(snapshotPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeploymentProfileSnapshot>(File.ReadAllText(snapshotPath))?.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
