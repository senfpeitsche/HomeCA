using System.Text.Json;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Notifications;

namespace HomeCA.Service.Operations;

/// <summary>
/// Creates encrypted backups on a schedule, keeps the newest ones, and optionally copies each backup to a second
/// directory such as a mounted NAS share. The second directory must already exist, so an unmounted share is
/// reported instead of silently filling the local mount point.
/// </summary>
public sealed class BackupScheduleService(HomeCaStorage storage, NotificationService notifications, ILogger<BackupScheduleService> logger)
{
    private const string BackupPattern = "homeca-*.hcab";
    private readonly string _path = Path.Combine(storage.RootPath, "state", "backup-schedule.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BackupSchedule> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task<BackupSchedule> SaveAsync(SaveBackupScheduleRequest request, CancellationToken ct)
    {
        if (request.IntervalHours is < 1 or > 168) throw new ArgumentException("The interval must be between 1 and 168 hours.");
        if (request.RetentionCount is < 1 or > 365) throw new ArgumentException("Between 1 and 365 backups can be kept.");
        var offsite = string.IsNullOrWhiteSpace(request.OffsiteDirectory) ? null : request.OffsiteDirectory.Trim();
        if (offsite is not null)
        {
            if (!Path.IsPathFullyQualified(offsite)) throw new ArgumentException("The second backup directory must be an absolute path.");
            var full = Path.GetFullPath(offsite);
            if (IsInside(full, Path.GetFullPath(storage.RootPath)) || IsInside(full, Path.GetFullPath(storage.ResolveBackupPath("."))))
                throw new ArgumentException("The second backup directory must be outside the HomeCA data and backup directories.");
            offsite = full;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var current = await ReadUnsafeAsync(ct);
            var updated = current with { Enabled = request.Enabled, IntervalHours = request.IntervalHours, RetentionCount = request.RetentionCount, OffsiteDirectory = offsite };
            await WriteUnsafeAsync(updated, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var schedule = await GetAsync(ct);
        return schedule.Enabled && (schedule.LastRunAt is null || now - schedule.LastRunAt >= TimeSpan.FromHours(schedule.IntervalHours));
    }

    /// <summary>Runs one scheduled backup now. Failures are recorded and notified, never thrown.</summary>
    public async Task<BackupSchedule> RunAsync(CancellationToken ct)
    {
        var schedule = await GetAsync(ct);
        var now = DateTimeOffset.UtcNow;
        try
        {
            var backup = await storage.CreateBackupAsync(ct);
            var pruned = Prune(Path.GetDirectoryName(backup.Path)!, schedule.RetentionCount);
            if (schedule.OffsiteDirectory is { } offsite)
            {
                if (!Directory.Exists(offsite)) throw new DirectoryNotFoundException($"The second backup directory '{offsite}' does not exist. Is the share mounted?");
                File.Copy(backup.Path, Path.Combine(offsite, backup.FileName), overwrite: false);
                pruned += Prune(offsite, schedule.RetentionCount);
            }
            logger.LogInformation("Scheduled backup {BackupFile} created; removed {Count} old backup(s)", backup.FileName, pruned);
            return await RecordAsync(current => current with { LastRunAt = now, LastSuccessAt = now, LastBackupFile = backup.FileName, LastError = null }, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Scheduled backup failed");
            await notifications.SendAsync(NotificationEvent.BackupFailed, string.Empty, exception.Message, ct);
            return await RecordAsync(current => current with { LastRunAt = now, LastError = exception.Message }, ct);
        }
    }

    /// <summary>Deletes the oldest scheduled backups beyond the retention count; file names sort by creation time.</summary>
    internal static int Prune(string directory, int keep)
    {
        var stale = Directory.EnumerateFiles(directory, BackupPattern)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (var path in stale) File.Delete(path);
        return stale.Count;
    }

    private async Task<BackupSchedule> RecordAsync(Func<BackupSchedule, BackupSchedule> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var updated = update(await ReadUnsafeAsync(ct));
            await WriteUnsafeAsync(updated, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private static bool IsInside(string path, string directory) =>
        (path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<BackupSchedule> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return BackupSchedule.Default;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<BackupSchedule>(stream, cancellationToken: ct) ?? BackupSchedule.Default;
    }

    private async Task WriteUnsafeAsync(BackupSchedule value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

/// <summary>Checks every 15 minutes whether a scheduled backup is due.</summary>
public sealed class BackupScheduleBackgroundService(BackupScheduleService backups, ILogger<BackupScheduleBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await backups.IsDueAsync(DateTimeOffset.UtcNow, stoppingToken)) await backups.RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Backup schedule check failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}

public sealed record SaveBackupScheduleRequest(bool Enabled, int IntervalHours = 24, int RetentionCount = 14, string? OffsiteDirectory = null);
public sealed record BackupSchedule(bool Enabled, int IntervalHours, int RetentionCount, string? OffsiteDirectory,
    DateTimeOffset? LastRunAt = null, DateTimeOffset? LastSuccessAt = null, string? LastBackupFile = null, string? LastError = null)
{
    public static BackupSchedule Default { get; } = new(false, 24, 14, null);
}
