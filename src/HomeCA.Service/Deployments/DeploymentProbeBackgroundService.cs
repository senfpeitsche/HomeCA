using HomeCA.Service.Notifications;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Checks every deployment target with a host a few times a day and notifies once when a target stops serving
/// the current certificate. A renewal resets a target's probe, so the next check reports the new state.
/// </summary>
public sealed class DeploymentProbeBackgroundService(
    DeploymentTargetRegistry targets,
    DeploymentProbeService probes,
    NotificationService notifications,
    ILogger<DeploymentProbeBackgroundService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAllAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Deployment target check failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    internal async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var target in (await targets.ListAsync(ct)).Where(target => !string.IsNullOrWhiteSpace(target.Host)))
        {
            var previous = target.LastProbe?.Status;
            var result = await probes.ProbeAsync(target, ct);
            await targets.RecordProbeAsync(target.Id, result, ct);
            if (result.Status == DeploymentProbeResult.Current || result.Status == previous) continue;
            await notifications.SendAsync(NotificationEvent.DeploymentOutdated, target.Name,
                $"{target.Host}:{target.Port} → {result.Status}{(result.Error is null ? string.Empty : $" ({result.Error})")}", ct);
        }
    }
}
