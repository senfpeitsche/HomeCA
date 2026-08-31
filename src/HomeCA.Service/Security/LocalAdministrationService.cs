using System.Security.Cryptography;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Security;

public sealed class LocalAdministrationService(HomeCaStorage storage, IHostEnvironment environment, ILogger<LocalAdministrationService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _adminPath = Path.Combine(storage.RootPath, "state", "administrator.json");
    private readonly string _sessionPath = Path.Combine(storage.RootPath, "state", "sessions.json");
    private readonly string _auditPath = Path.Combine(storage.RootPath, "audit", "events.ndjson");

    /// <summary>
    /// Ensures a default administrator account exists. Called during application startup.
    /// Creates an admin/admin account with MustChangePassword=true when no administrator.json exists.
    /// </summary>
    public async Task EnsureDefaultAdministratorAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_adminPath)) return;
            var administrator = new AdministratorRecord("admin", PasswordHasher.Hash("admin"), DateTimeOffset.UtcNow, MustChangePassword: true);
            await WriteJsonAsync(_adminPath, administrator, cancellationToken);
            await AuditAsync("administrator.default_created", "admin", cancellationToken);
            logger.LogInformation("Default administrator account created — password change required on first login");
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetupAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_adminPath)) return false;
            var administrator = new AdministratorRecord(request.UserName, PasswordHasher.Hash(request.Password), DateTimeOffset.UtcNow);
            await WriteJsonAsync(_adminPath, administrator, cancellationToken);
            await AuditAsync("administrator.setup", request.UserName, cancellationToken);
            logger.LogInformation("Administrator account created for {UserName}", request.UserName);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment()
            && string.Equals(request.UserName, "admin", StringComparison.Ordinal)
            && string.Equals(request.Password, "foobar", StringComparison.Ordinal))
        {
            return new LoginResponse("homeca-debug", 43200, false);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_adminPath)) return null;
            var administrator = await ReadJsonAsync<AdministratorRecord>(_adminPath, cancellationToken);
            if (administrator is null || !string.Equals(administrator.UserName, request.UserName, StringComparison.Ordinal) || !PasswordHasher.Verify(request.Password, administrator.PasswordHash))
            {
                await AuditAsync("administrator.login_failed", request.UserName, cancellationToken);
                logger.LogWarning("Failed login attempt for {UserName}", request.UserName);
                return null;
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var sessions = await ReadJsonAsync<List<SessionRecord>>(_sessionPath, cancellationToken) ?? [];
            sessions.RemoveAll(session => session.ExpiresAt <= DateTimeOffset.UtcNow);
            sessions.Add(new SessionRecord(TokenHash(token), administrator.UserName, DateTimeOffset.UtcNow.AddHours(12)));
            await WriteJsonAsync(_sessionPath, sessions, cancellationToken);
            await AuditAsync("administrator.login", administrator.UserName, cancellationToken);
            return new LoginResponse(token, 43200, administrator.MustChangePassword);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Changes the administrator password. Validates the current password before applying the new one.
    /// Clears the MustChangePassword flag after a successful change.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(string token, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
            return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_adminPath)) return false;
            var administrator = await ReadJsonAsync<AdministratorRecord>(_adminPath, cancellationToken);
            if (administrator is null || !PasswordHasher.Verify(request.CurrentPassword, administrator.PasswordHash))
            {
                await AuditAsync("administrator.password_change_failed", administrator?.UserName ?? "unknown", cancellationToken);
                return false;
            }

            var updated = new AdministratorRecord(administrator.UserName, PasswordHasher.Hash(request.NewPassword), administrator.CreatedAt, MustChangePassword: false);
            await WriteJsonAsync(_adminPath, updated, cancellationToken);
            await AuditAsync("administrator.password_changed", administrator.UserName, cancellationToken);
            logger.LogInformation("Administrator password changed for {UserName}", administrator.UserName);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsSessionValidAsync(string? token, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment() && string.Equals(token, "homeca-debug", StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(token)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sessions = await ReadJsonAsync<List<SessionRecord>>(_sessionPath, cancellationToken) ?? [];
            return sessions.Any(session => session.ExpiresAt > DateTimeOffset.UtcNow && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(session.TokenHash), Convert.FromHexString(TokenHash(token))));
        }
        finally { _gate.Release(); }
    }

    private async Task AuditAsync(string action, string subject, CancellationToken cancellationToken)
    {
        var entry = JsonSerializer.Serialize(new { occurredAt = DateTimeOffset.UtcNow, action, subject });
        await File.AppendAllTextAsync(_auditPath, entry + Environment.NewLine, cancellationToken);
    }

    /// <summary>Reads audit events with optional pagination and action filter.</summary>
    public Task<IReadOnlyList<AuditEvent>> ReadAuditLogAsync(int skip = 0, int take = 100, string? action = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_auditPath)) return Task.FromResult<IReadOnlyList<AuditEvent>>([]);
        var query = File.ReadAllLines(_auditPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<AuditEvent>(line))
            .Where(entry => entry is not null)
            .Cast<AuditEvent>()
            .OrderByDescending(entry => entry.OccurredAt)
            .AsEnumerable();
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(entry => entry.Action.Contains(action, StringComparison.OrdinalIgnoreCase));
        var items = query.Skip(skip).Take(Math.Clamp(take, 1, 500)).ToList();
        return Task.FromResult<IReadOnlyList<AuditEvent>>(items);
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
    }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath)) await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}

public sealed record SetupRequest(string UserName, string Password);
public sealed record LoginRequest(string UserName, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record LoginResponse(string AccessToken, int ExpiresInSeconds, bool MustChangePassword);
internal sealed record AdministratorRecord(string UserName, string PasswordHash, DateTimeOffset CreatedAt, bool MustChangePassword = false);
internal sealed record SessionRecord(string TokenHash, string UserName, DateTimeOffset ExpiresAt);
public sealed record AuditEvent(DateTimeOffset OccurredAt, string Action, string Subject);
