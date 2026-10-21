using System.Text.Json;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Localization;

namespace HomeCA.Service.Notifications;

/// <summary>
/// Stores push and webhook channels for notifications next to the e-mail settings. Tokens are write-only:
/// the read model only tells whether one is stored, and saving without a token keeps the stored one.
/// </summary>
public sealed class NotificationChannelRegistry(HomeCaStorage storage)
{
    public const string Ntfy = "ntfy";
    public const string Gotify = "gotify";
    public const string Webhook = "webhook";
    private static readonly string[] Types = [Ntfy, Gotify, Webhook];

    private readonly string _path = Path.Combine(storage.RootPath, "state", "notification-channels.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<NotificationSettings> GetAsync(CancellationToken ct) => (await GetStoredAsync(ct)).ToPublic();

    public async Task<NotificationSettings> SaveAsync(SaveNotificationSettingsRequest request, CancellationToken ct)
    {
        var language = LocaleCatalog.NormalizeOrDefault(request.Language);
        await _gate.WaitAsync(ct);
        try
        {
            var existing = await ReadUnsafeAsync(ct);
            var channels = request.Channels.Select(channel => Normalize(channel, existing.Channels)).ToList();
            if (channels.GroupBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new ArgumentException("Channel names must be unique.");
            var stored = new StoredNotificationSettings(language, channels);
            await WriteAtomicAsync(stored, ct);
            return stored.ToPublic();
        }
        finally { _gate.Release(); }
    }

    internal async Task<StoredNotificationSettings> GetStoredAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    private static StoredNotificationChannel Normalize(SaveNotificationChannelRequest channel, IReadOnlyList<StoredNotificationChannel> existing)
    {
        var type = channel.Type.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) throw new ArgumentException("Channel type must be ntfy, gotify or webhook.");
        if (string.IsNullOrWhiteSpace(channel.Name)) throw new ArgumentException("Every channel needs a name.");
        if (!Uri.TryCreate(channel.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Channel '{channel.Name.Trim()}' needs an absolute http or https URL.");
        var topic = string.IsNullOrWhiteSpace(channel.Topic) ? null : channel.Topic.Trim();
        if (type == Ntfy && (topic is null || topic.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_'))))
            throw new ArgumentException("An ntfy channel needs a topic of letters, digits, '-' or '_'.");

        var id = string.IsNullOrWhiteSpace(channel.Id) ? Guid.NewGuid().ToString("N") : channel.Id.Trim();
        var previous = existing.FirstOrDefault(item => item.Id == id);
        var token = string.IsNullOrWhiteSpace(channel.Token) ? previous?.Token : channel.Token.Trim();
        if (type == Gotify && string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A Gotify channel needs an application token.");
        var events = (channel.Events ?? [])
            .Select(name => Enum.TryParse<NotificationEvent>(name, true, out var parsed) ? parsed : throw new ArgumentException($"Unknown event '{name}'."))
            .Where(item => item != NotificationEvent.Test)
            .Distinct()
            .ToList();
        return new StoredNotificationChannel(id, type, channel.Name.Trim(), url.ToString().TrimEnd('/'), type == Ntfy ? topic : null, token, channel.Enabled, events);
    }

    private async Task<StoredNotificationSettings> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return StoredNotificationSettings.Empty;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<StoredNotificationSettings>(stream, cancellationToken: ct) ?? StoredNotificationSettings.Empty;
    }

    private async Task WriteAtomicAsync(StoredNotificationSettings value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

public enum NotificationEvent
{
    CertificateRenewed,
    RenewalFailed,
    DeploymentOutdated,
    BackupFailed,
    Test,
    // Appended after Test: stored channels keep events as numbers.
    DeploymentPushFailed
}

public sealed record SaveNotificationSettingsRequest(string? Language, IReadOnlyList<SaveNotificationChannelRequest> Channels);
public sealed record SaveNotificationChannelRequest(string? Id, string Type, string Name, string Url, string? Topic = null, string? Token = null, bool Enabled = true, IReadOnlyList<string>? Events = null);
public sealed record NotificationSettings(string Language, IReadOnlyList<NotificationChannel> Channels);
/// <summary>Read model of a channel. <see cref="Events"/> empty means all events.</summary>
public sealed record NotificationChannel(string Id, string Type, string Name, string Url, string? Topic, bool HasToken, bool Enabled, IReadOnlyList<string> Events);

internal sealed record StoredNotificationSettings(string Language, IReadOnlyList<StoredNotificationChannel> Channels)
{
    public static StoredNotificationSettings Empty { get; } = new(LocaleCatalog.DefaultCulture, []);
    public NotificationSettings ToPublic() => new(Language, Channels.Select(channel => channel.ToPublic()).ToList());
}

internal sealed record StoredNotificationChannel(string Id, string Type, string Name, string Url, string? Topic, string? Token, bool Enabled, IReadOnlyList<NotificationEvent> Events)
{
    public bool Receives(NotificationEvent notificationEvent) => notificationEvent == NotificationEvent.Test || Events.Count == 0 || Events.Contains(notificationEvent);
    public NotificationChannel ToPublic() => new(Id, Type, Name, Url, Topic, !string.IsNullOrWhiteSpace(Token), Enabled, Events.Select(item => item.ToString()).ToList());
}
