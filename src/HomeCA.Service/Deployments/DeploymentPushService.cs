using System.Threading.Channels;
using HomeCA.Service.Notifications;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Starts the agent of a target over SSH right after a renewal instead of waiting for the hourly timer. HomeCA signs
/// in as root with its push key, which the device accepts only with a forced command that starts the agent.
/// </summary>
public sealed class DeploymentPushService(
    ISshClientFactory ssh,
    SshPushKeyService keys,
    DeploymentTargetRegistry targets,
    DeploymentProbeService probes,
    NotificationService notifications,
    ILogger<DeploymentPushService> logger)
{
    /// <summary>Exit code of the agent when another run holds its lock.</summary>
    public const int AgentBusyExitCode = 75;

    private static readonly TimeSpan PushTimeout = TimeSpan.FromMinutes(10);
    // A run that was already going may have fetched the manifest before the renewal; try again once it ended.
    internal TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    private const int BusyAttempts = 4;
    private const int MaximumOutputLength = 16 * 1024;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    internal ChannelReader<string> Queue => _queue.Reader;

    /// <summary>Queues a push for a renewed target, so a renewal never waits for a device.</summary>
    public void Enqueue(DeploymentTarget target)
    {
        if (target.Ssh is { PushEnabled: true }) _queue.Writer.TryWrite(target.Id);
    }

    /// <summary>Pushes a queued target and notifies when the device could not be updated.</summary>
    internal async Task PushQueuedAsync(string targetId, CancellationToken ct)
    {
        var target = await targets.GetAsync(targetId, ct);
        if (target?.Ssh is not { PushEnabled: true }) return;
        var result = await PushAsync(target, ct);
        for (var attempt = 1; attempt < BusyAttempts && result.Status == DeploymentPushResult.Busy; attempt++)
        {
            await Task.Delay(BusyRetryDelay, ct);
            result = await PushAsync(target, ct);
        }
        if (result.Status == DeploymentPushResult.Failed)
            await notifications.SendAsync(NotificationEvent.DeploymentPushFailed, target.Name, $"{target.Ssh.Host}:{target.Ssh.Port} → {LastLine(result.Output)}", ct);
    }

    /// <summary>Starts the agent now. Returns null when the target does not exist or was never set up over SSH.</summary>
    public async Task<DeploymentPushResult?> PushAsync(string targetId, CancellationToken ct) =>
        await targets.GetAsync(targetId, ct) is { Ssh: not null } target ? await PushAsync(target, ct) : null;

    internal async Task<DeploymentPushResult> PushAsync(DeploymentTarget target, CancellationToken ct)
    {
        var info = target.Ssh ?? throw new InvalidOperationException("The target was not set up over SSH.");
        DeploymentPushResult result;
        try
        {
            var credentials = new SshCredentials(info.Host, info.Port, "root", PrivateKey: await keys.GetPrivateKeyPemAsync(ct));
            using var session = await ssh.ConnectAsync(credentials, info.HostKeyFingerprint, ct);
            // The forced command on the device replaces whatever is sent here.
            var run = await session.RunAsync("homeca-agent", null, PushTimeout, ct);
            var output = Trim(string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Output.TrimEnd() + "\n" + run.Error);
            var status = run.ExitCode switch
            {
                0 => DeploymentPushResult.Ok,
                AgentBusyExitCode => DeploymentPushResult.Busy,
                _ => DeploymentPushResult.Failed
            };
            result = new DeploymentPushResult(DateTimeOffset.UtcNow, status, run.ExitCode, output);
        }
        catch (SshHostKeyNotTrustedException ex)
        {
            result = new DeploymentPushResult(DateTimeOffset.UtcNow, DeploymentPushResult.Failed,
                Output: $"The host key {ex.Presented.Fingerprint} differs from the confirmed host key {info.HostKeyFingerprint}; HomeCA did not sign in.");
        }
        catch (SshSetupConnectionException ex)
        {
            result = new DeploymentPushResult(DateTimeOffset.UtcNow, DeploymentPushResult.Failed, Output: ex.Message);
        }
        await targets.RecordPushAsync(target.Id, result, ct);
        logger.LogInformation("Push to deployment target {TargetId} finished with {Status}", target.Id, result.Status);
        if (result.Status == DeploymentPushResult.Ok && !string.IsNullOrWhiteSpace(target.Host)) await probes.ProbeAsync(target.Id, ct);
        return result;
    }

    private static string Trim(string text) => text.Length <= MaximumOutputLength ? text : text[..MaximumOutputLength] + "\n…";

    private static string LastLine(string? output) =>
        output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
}

/// <summary>Works through queued pushes one at a time.</summary>
public sealed class DeploymentPushBackgroundService(DeploymentPushService pushes, ILogger<DeploymentPushBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var targetId in pushes.Queue.ReadAllAsync(stoppingToken))
        {
            try { await pushes.PushQueuedAsync(targetId, stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Push to deployment target {TargetId} failed", targetId);
            }
        }
    }
}
