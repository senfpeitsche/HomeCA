using System.Net.Http.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Tells a deployment target that a renewed certificate is ready. The payload carries no secrets; the target
/// fetches the files itself with its token.
/// </summary>
public sealed class DeploymentWebhookService(IHttpClientFactory httpClientFactory, HomeCaStorage storage, DeploymentProbeService probes, ILogger<DeploymentWebhookService> logger)
{
    public async Task NotifyRenewedAsync(DeploymentTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.WebhookUrl)) return;
        var client = httpClientFactory.CreateClient(nameof(DeploymentWebhookService));
        client.Timeout = TimeSpan.FromSeconds(10);
        var payload = new DeploymentWebhookPayload(
            "certificate.renewed",
            target.Id,
            target.Name,
            target.CertificateId,
            probes.CertificateFingerprint(target.CertificateId),
            DeploymentAgentScript.ManifestUrl(storage.PublicUrl, target.Id));
        try
        {
            using var response = await client.PostAsJsonAsync(target.WebhookUrl, payload, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Webhook of deployment target {TargetId} answered {StatusCode}", target.Id, (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The renewal itself succeeded; the agent still picks the certificate up on its next run.
            logger.LogWarning("Webhook of deployment target {TargetId} could not be delivered: {Reason}", target.Id, exception.Message);
        }
    }
}

public sealed record DeploymentWebhookPayload(string Event, string TargetId, string TargetName, string CertificateId, string? Fingerprint, string ManifestUrl);
