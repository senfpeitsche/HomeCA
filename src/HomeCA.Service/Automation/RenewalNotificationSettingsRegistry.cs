using System.Net.Mail;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Automation;

/// <summary>Stores delivery settings for renewal e-mails. Secret fields are never exposed by the read model.</summary>
public sealed class RenewalNotificationSettingsRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "renewal-notifications.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RenewalNotificationSettings> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadUnsafeAsync(ct)).ToPublic(); }
        finally { _gate.Release(); }
    }

    public async Task<RenewalNotificationSettings> UpdateAsync(UpdateRenewalNotificationSettingsRequest request, CancellationToken ct)
    {
        var provider = request.Provider.Trim().ToLowerInvariant();
        if (provider is not ("smtp" or "m365")) throw new ArgumentException("Provider must be 'smtp' or 'm365'.");

        var recipients = request.Recipients
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var recipient in recipients) _ = new MailAddress(recipient);

        await _gate.WaitAsync(ct);
        try
        {
            var existing = await ReadUnsafeAsync(ct);
            var smtpPassword = string.IsNullOrWhiteSpace(request.SmtpPassword) ? existing.SmtpPassword : request.SmtpPassword;
            var m365ClientSecret = string.IsNullOrWhiteSpace(request.M365ClientSecret) ? existing.M365ClientSecret : request.M365ClientSecret;
            var updated = new StoredRenewalNotificationSettings(
                request.Enabled,
                provider,
                recipients,
                request.FromAddress?.Trim() ?? string.Empty,
                request.SmtpHost?.Trim() ?? string.Empty,
                request.SmtpPort ?? 587,
                request.SmtpUserName?.Trim() ?? string.Empty,
                smtpPassword ?? string.Empty,
                request.M365TenantId?.Trim() ?? string.Empty,
                request.M365ClientId?.Trim() ?? string.Empty,
                m365ClientSecret ?? string.Empty,
                request.M365SenderMailbox?.Trim() ?? string.Empty);
            ValidateEnabled(updated);
            await WriteAtomicAsync(updated, ct);
            return updated.ToPublic();
        }
        finally { _gate.Release(); }
    }

    internal async Task<StoredRenewalNotificationSettings> GetStoredAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    private static void ValidateEnabled(StoredRenewalNotificationSettings settings)
    {
        if (!settings.Enabled) return;
        if (settings.Recipients.Count == 0) throw new ArgumentException("At least one recipient is required when e-mail notifications are enabled.");
        if (settings.Provider == "smtp")
        {
            if (string.IsNullOrWhiteSpace(settings.FromAddress) || string.IsNullOrWhiteSpace(settings.SmtpHost) || settings.SmtpPort is < 1 or > 65535)
                throw new ArgumentException("SMTP sender address, host and a valid port are required.");
        }
        else if (string.IsNullOrWhiteSpace(settings.M365TenantId) || string.IsNullOrWhiteSpace(settings.M365ClientId) || string.IsNullOrWhiteSpace(settings.M365ClientSecret) || string.IsNullOrWhiteSpace(settings.M365SenderMailbox))
        {
            throw new ArgumentException("Microsoft 365 tenant ID, client ID, client secret and sender mailbox are required.");
        }
    }

    private async Task<StoredRenewalNotificationSettings> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return StoredRenewalNotificationSettings.Disabled;
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<StoredRenewalNotificationSettings>(stream, cancellationToken: ct) ?? StoredRenewalNotificationSettings.Disabled;
    }

    private async Task WriteAtomicAsync(StoredRenewalNotificationSettings value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

public sealed record UpdateRenewalNotificationSettingsRequest(
    bool Enabled,
    string Provider,
    IReadOnlyList<string> Recipients,
    string? FromAddress = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    string? SmtpUserName = null,
    string? SmtpPassword = null,
    string? M365TenantId = null,
    string? M365ClientId = null,
    string? M365ClientSecret = null,
    string? M365SenderMailbox = null);

public sealed record RenewalNotificationSettings(
    bool Enabled,
    string Provider,
    IReadOnlyList<string> Recipients,
    string FromAddress,
    string SmtpHost,
    int SmtpPort,
    string SmtpUserName,
    bool HasSmtpPassword,
    string M365TenantId,
    string M365ClientId,
    bool HasM365ClientSecret,
    string M365SenderMailbox);

internal sealed record StoredRenewalNotificationSettings(
    bool Enabled,
    string Provider,
    IReadOnlyList<string> Recipients,
    string FromAddress,
    string SmtpHost,
    int SmtpPort,
    string SmtpUserName,
    string SmtpPassword,
    string M365TenantId,
    string M365ClientId,
    string M365ClientSecret,
    string M365SenderMailbox)
{
    public static StoredRenewalNotificationSettings Disabled { get; } = new(false, "smtp", [], "", "", 587, "", "", "", "", "", "");

    public RenewalNotificationSettings ToPublic() => new(Enabled, Provider, Recipients, FromAddress, SmtpHost, SmtpPort, SmtpUserName,
        !string.IsNullOrWhiteSpace(SmtpPassword), M365TenantId, M365ClientId, !string.IsNullOrWhiteSpace(M365ClientSecret), M365SenderMailbox);
}
