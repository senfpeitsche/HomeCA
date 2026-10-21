using HomeCA.Service.Automation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using HomeCA.Service.Revocation;

namespace HomeCA.Service.Operations;

/// <summary>
/// Collects everything an administrator should look at: certificates about to expire without renewal, failed
/// renewals, devices that do not serve their current certificate, silent agents, missing backups and expiring CAs.
/// Items carry a kind and raw values; the UI localizes them.
/// </summary>
public sealed class AttentionService(
    HomeCaStorage storage,
    CertificateIssuanceService certificates,
    CertificateAuthorityService authorities,
    RevocationRegistry revocations,
    RenewalPlanRegistry plans,
    DeploymentTargetRegistry targets,
    BackupScheduleService backups)
{
    internal static readonly TimeSpan ExpiryWindow = TimeSpan.FromDays(30);
    internal static readonly TimeSpan AgentSilence = TimeSpan.FromDays(2);
    internal static readonly TimeSpan BackupAge = TimeSpan.FromDays(7);
    internal static readonly TimeSpan AuthorityWindow = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<AttentionItem>> GetAsync(CancellationToken ct) => await GetAsync(DateTimeOffset.UtcNow, ct);

    internal async Task<IReadOnlyList<AttentionItem>> GetAsync(DateTimeOffset now, CancellationToken ct)
    {
        var items = new List<AttentionItem>();
        var revoked = (await revocations.ListAsync(ct)).Select(revocation => revocation.SerialNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = (await certificates.ListAsync(ct, take: 500)).Where(certificate => !revoked.Contains(certificate.Id)).ToList();
        var allPlans = await plans.ListAsync(ct);
        var planned = allPlans.Where(plan => plan.Enabled).Select(plan => plan.CertificateId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A renewed certificate stays in the inventory until it expires; only the newest one per subject counts.
        foreach (var certificate in active.GroupBy(certificate => certificate.Subject, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(item => item.ExpiresAt).ThenBy(item => item.ValidFrom).Last()))
        {
            var expiresAt = new DateTimeOffset(certificate.ExpiresAt.ToUniversalTime(), TimeSpan.Zero);
            if (expiresAt <= now)
                items.Add(new AttentionItem(AttentionItem.Error, "certificate-expired", certificate.Subject, expiresAt.ToString("yyyy-MM-dd"), "certificates", certificate.Id));
            else if (expiresAt - now <= ExpiryWindow && !planned.Contains(certificate.Id))
                items.Add(new AttentionItem(AttentionItem.Warning, "certificate-expiring", certificate.Subject, ((int)(expiresAt - now).TotalDays).ToString(), "certificates", certificate.Id));
        }

        var subjects = active.ToDictionary(certificate => certificate.Id, certificate => certificate.Subject, StringComparer.OrdinalIgnoreCase);
        foreach (var plan in allPlans.Where(plan => plan.Enabled && plan.LastError is not null && (plan.LastRenewedAt is null || plan.LastFailedAt > plan.LastRenewedAt)))
            items.Add(new AttentionItem(AttentionItem.Error, "renewal-failed", subjects.GetValueOrDefault(plan.CertificateId, plan.CertificateId), plan.LastError, "automation", plan.CertificateId));

        foreach (var target in await targets.ListAsync(ct))
        {
            if (target.LastProbe is { } probe && probe.Status != DeploymentProbeResult.Current)
                items.Add(new AttentionItem(probe.Status == DeploymentProbeResult.Unreachable ? AttentionItem.Warning : AttentionItem.Error, $"deployment-{probe.Status}", target.Name, probe.Error, "deployments", target.Id));
            else if (target.LastCheckInAt is { } checkIn && now - checkIn > AgentSilence)
                items.Add(new AttentionItem(AttentionItem.Warning, "agent-silent", target.Name, checkIn.ToString("yyyy-MM-dd HH:mm"), "deployments", target.Id));
            else if (target.LastCheckInAt is null && now - target.CreatedAt > TimeSpan.FromDays(1))
                items.Add(new AttentionItem(AttentionItem.Info, "agent-missing", target.Name, null, "deployments", target.Id));
        }

        var schedule = await backups.GetAsync(ct);
        if (schedule.Enabled && schedule.LastError is not null && (schedule.LastSuccessAt is null || schedule.LastRunAt > schedule.LastSuccessAt))
            items.Add(new AttentionItem(AttentionItem.Error, "backup-failed", string.Empty, schedule.LastError, "operations", null));
        else if (NewestBackup() is not { } newest || now - newest > BackupAge)
            items.Add(new AttentionItem(AttentionItem.Warning, "backup-missing", string.Empty, NewestBackup()?.ToString("yyyy-MM-dd"), "operations", null));

        foreach (var authority in (await authorities.ListAsync(ct)).Where(authority => authority.IsActive && !authority.IsRevoked))
        {
            var expiresAt = new DateTimeOffset(authority.ExpiresAt.ToUniversalTime(), TimeSpan.Zero);
            if (expiresAt - now <= AuthorityWindow)
                items.Add(new AttentionItem(AttentionItem.Warning, "authority-expiring", authority.Name, expiresAt.ToString("yyyy-MM-dd"), "authorities", authority.Id));
        }

        return items.OrderBy(item => item.Severity switch { AttentionItem.Error => 0, AttentionItem.Warning => 1, _ => 2 }).ToList();
    }

    private DateTimeOffset? NewestBackup()
    {
        var directory = storage.ResolveBackupPath(".");
        if (!Directory.Exists(directory)) return null;
        var newest = Directory.EnumerateFiles(directory, "*.hcab").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        return newest == default ? null : new DateTimeOffset(newest, TimeSpan.Zero);
    }
}

/// <summary>One thing that needs attention. <see cref="Section"/> names the UI area that resolves it.</summary>
public sealed record AttentionItem(string Severity, string Kind, string Subject, string? Detail, string Section, string? ReferenceId)
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}
