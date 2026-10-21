using HomeCA.Service.Deployments;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;

namespace HomeCA.Service.Automation;

/// <summary>
/// Renews one certificate with its original settings and moves everything that follows it — renewal plans,
/// deployment targets and HomeCA's own TLS certificate — to the successor. Used by the hourly renewal check
/// and by the "renew now" action.
/// </summary>
public sealed class CertificateRenewalService(
    HomeCaStorage storage,
    CertificateIssuanceService certificates,
    RenewalPlanRegistry plans,
    DeploymentTargetRegistry targets,
    DeploymentWebhookService webhooks,
    DeploymentPushService pushes,
    TlsActivationService tls,
    ILogger<CertificateRenewalService> logger)
{
    public async Task<IssueResult> RenewAsync(string certificateId, CancellationToken ct)
    {
        var request = RenewalBackgroundService.BuildRenewalRequest(storage, certificateId)
            ?? throw new InvalidOperationException("This certificate cannot be renewed by HomeCA: its private key is held elsewhere (CSR or ACME) or it has no SANs.");

        var result = await certificates.IssueAsync(request, ct);
        await plans.ReplaceCertificateAsync(certificateId, result.Id, ct);
        var movedTargets = await targets.ReplaceCertificateAsync(certificateId, result.Id, ct);
        logger.LogInformation("Renewed certificate {OldId} → {NewId} ({Subject}); moved {TargetCount} deployment target(s)",
            certificateId, result.Id, result.Subject, movedTargets.Count);

        try
        {
            // If this was HomeCA's own TLS certificate, serve the replacement.
            await tls.OnCertificateRenewedAsync(certificateId, result.Id, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The renewal itself succeeded and the plans already point to the new certificate.
            logger.LogError(exception, "Renewed certificate {CertificateId} could not be installed as the HomeCA TLS certificate", result.Id);
        }

        foreach (var target in movedTargets)
        {
            await webhooks.NotifyRenewedAsync(target, ct);
            pushes.Enqueue(target);
        }

        return result;
    }
}
