using System.Security.Cryptography;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Security;

public sealed class LocalAdministrationService(HomeCaStorage storage, IHostEnvironment environment, ILogger<LocalAdministrationService> logger)
{
    private readonly IHostEnvironment _environment = environment;
    // Serializes writes of administrator.json and sessions.json. Reads use the immutable snapshot.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _auditGate = new(1, 1);
    private readonly string _adminPath = Path.Combine(storage.RootPath, "state", "administrator.json");
    private readonly string _sessionPath = Path.Combine(storage.RootPath, "state", "sessions.json");
    private readonly string _auditPath = Path.Combine(storage.RootPath, "audit", "events.ndjson");
    // Audit lines are written with camelCase names from an anonymous type.
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);
    // This process is the only writer of the state files, so a snapshot updated on every write stays
    // current. It lets every API request validate its token without file I/O or the write lock.
    private volatile StateSnapshot? _snapshot;

    /// <summary>
    /// Ensures a default administrator account exists. Called during application startup.
    /// Creates an admin/admin account with MustChangePassword=true when no administrator.json exists.
    /// </summary>
    public async Task EnsureDefaultAdministratorAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await GetSnapshotUnsafeAsync(cancellationToken);
            if (snapshot.Administrator is not null) return;
            var administrator = new AdministratorRecord("admin", PasswordHasher.Hash("admin"), DateTimeOffset.UtcNow, MustChangePassword: true);
            await WriteJsonAsync(_adminPath, administrator, cancellationToken);
            _snapshot = snapshot with { Administrator = administrator };
            await AuditAsync("administrator.default_created", "admin", cancellationToken);
            logger.LogInformation("Default administrator account created — password change required on first login");
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetupAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        var passwordHash = PasswordHasher.Hash(request.Password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await GetSnapshotUnsafeAsync(cancellationToken);
            if (snapshot.Administrator is not null) return false;
            var administrator = new AdministratorRecord(request.UserName, passwordHash, DateTimeOffset.UtcNow);
            await WriteJsonAsync(_adminPath, administrator, cancellationToken);
            _snapshot = snapshot with { Administrator = administrator };
            await AuditAsync("administrator.setup", request.UserName, cancellationToken);
            logger.LogInformation("Administrator account created for {UserName}", request.UserName);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
#if DEBUG
        if (_environment.IsDevelopment()
            && string.Equals(request.UserName, "admin", StringComparison.Ordinal)
            && string.Equals(request.Password, "foobar", StringComparison.Ordinal))
        {
            return new LoginResponse("homeca-debug", 43200, false);
        }
#endif

        // PBKDF2 with 600k iterations takes a noticeable time; it runs outside the write lock so a
        // login (or a password-guessing client) cannot stall every other API request.
        var administrator = (await GetSnapshotAsync(cancellationToken)).Administrator;
        if (administrator is null) return null;
        // Always verify, also for a wrong user name, so the response time does not reveal it.
        var passwordMatches = PasswordHasher.Verify(request.Password, administrator.PasswordHash);
        if (!passwordMatches || !string.Equals(administrator.UserName, request.UserName, StringComparison.Ordinal))
        {
            await AuditAsync("administrator.login_failed", request.UserName, cancellationToken);
            logger.LogWarning("Failed login attempt for {UserName}", request.UserName);
            return null;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await GetSnapshotUnsafeAsync(cancellationToken);
            // The password may have changed while it was being verified.
            if (snapshot.Administrator?.PasswordHash != administrator.PasswordHash) return null;
            var sessions = snapshot.Sessions.Where(session => session.ExpiresAt > DateTimeOffset.UtcNow).ToList();
            sessions.Add(new SessionRecord(TokenHash(token), administrator.UserName, DateTimeOffset.UtcNow.AddHours(12)));
            await WriteJsonAsync(_sessionPath, sessions, cancellationToken);
            _snapshot = snapshot with { Sessions = sessions };
        }
        finally { _gate.Release(); }
        await AuditAsync("administrator.login", administrator.UserName, cancellationToken);
        return new LoginResponse(token, 43200, administrator.MustChangePassword);
    }

    /// <summary>
    /// Changes the administrator password. Validates the current password before applying the new one,
    /// clears the MustChangePassword flag and ends every session except the one making the change.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(string token, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
            return false;

        var administrator = (await GetSnapshotAsync(cancellationToken)).Administrator;
        if (administrator is null) return false;
        if (!PasswordHasher.Verify(request.CurrentPassword, administrator.PasswordHash))
        {
            await AuditAsync("administrator.password_change_failed", administrator.UserName, cancellationToken);
            logger.LogWarning("Failed password change for {UserName}: the current password did not match", administrator.UserName);
            return false;
        }
        var newHash = PasswordHasher.Hash(request.NewPassword);

        int endedSessions;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await GetSnapshotUnsafeAsync(cancellationToken);
            if (snapshot.Administrator?.PasswordHash != administrator.PasswordHash) return false;
            var updated = administrator with { PasswordHash = newHash, MustChangePassword = false };
            await WriteJsonAsync(_adminPath, updated, cancellationToken);

            // Sessions opened with the old password (possibly by someone who knew it) must not survive.
            var currentHash = IsValidToken(token) ? TokenHash(token) : null;
            var remaining = snapshot.Sessions
                .Where(session => session.ExpiresAt > DateTimeOffset.UtcNow && session.TokenHash == currentHash)
                .ToList();
            endedSessions = snapshot.Sessions.Count - remaining.Count;
            await WriteJsonAsync(_sessionPath, remaining, cancellationToken);
            _snapshot = new StateSnapshot(updated, remaining);
        }
        finally { _gate.Release(); }
        await AuditAsync("administrator.password_changed", administrator.UserName, cancellationToken);
        logger.LogInformation("Administrator password changed for {UserName}; ended {Count} other session(s)", administrator.UserName, endedSessions);
        return true;
    }

    /// <summary>Validates a session and exposes whether it is restricted to a mandatory password change.</summary>
    public async Task<SessionValidation> ValidateSessionAsync(string? token, CancellationToken cancellationToken)
    {
#if DEBUG
        if (_environment.IsDevelopment() && string.Equals(token, "homeca-debug", StringComparison.Ordinal)) return new(true, false);
#endif
        if (string.IsNullOrWhiteSpace(token)) return new(false, false);
        if (!IsValidToken(token)) return new(false, false);
        var tokenHash = Convert.FromHexString(TokenHash(token));
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var isValid = snapshot.Sessions.Any(session => session.ExpiresAt > DateTimeOffset.UtcNow && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(session.TokenHash), tokenHash));
        if (!isValid || snapshot.Administrator is null) return new(false, false);
        return new(true, snapshot.Administrator.MustChangePassword);
    }

    private async Task<StateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is { } snapshot) return snapshot;
        await _gate.WaitAsync(cancellationToken);
        try { return await GetSnapshotUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the snapshot, loading it from disk once; the caller must hold <c>_gate</c>.</summary>
    private async Task<StateSnapshot> GetSnapshotUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is { } snapshot) return snapshot;
        var administrator = await ReadJsonAsync<AdministratorRecord>(_adminPath, cancellationToken);
        var sessions = await ReadJsonAsync<List<SessionRecord>>(_sessionPath, cancellationToken) ?? [];
        return _snapshot = new StateSnapshot(administrator, sessions);
    }

    private async Task AuditAsync(string action, string subject, CancellationToken cancellationToken)
    {
        var entry = JsonSerializer.Serialize(new { occurredAt = DateTimeOffset.UtcNow, action, subject });
        // Concurrent appends from parallel requests would otherwise interleave or collide.
        await _auditGate.WaitAsync(cancellationToken);
        try { await File.AppendAllTextAsync(_auditPath, entry + Environment.NewLine, cancellationToken); }
        finally { _auditGate.Release(); }
    }

    /// <summary>Reads audit events with optional pagination and action filter.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ReadAuditLogAsync(int skip = 0, int take = 100, string? action = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_auditPath)) return [];
        string[] lines;
        await _auditGate.WaitAsync(cancellationToken);
        try { lines = await File.ReadAllLinesAsync(_auditPath, cancellationToken); }
        finally { _auditGate.Release(); }
        var query = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(TryParseAuditEvent)
            .Where(entry => entry is not null)
            .Cast<AuditEvent>()
            .OrderByDescending(entry => entry.OccurredAt)
            .AsEnumerable();
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(entry => entry.Action?.Contains(action, StringComparison.OrdinalIgnoreCase) == true);
        return query.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToList();
    }

    private AuditEvent? TryParseAuditEvent(string line)
    {
        try { return JsonSerializer.Deserialize<AuditEvent>(line, AuditJsonOptions); }
        catch (JsonException exception)
        {
            // A torn or hand-edited line must not make the whole audit log unreadable.
            logger.LogWarning(exception, "Skipping unreadable audit log line");
            return null;
        }
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    private static bool IsValidToken(string token)
    {
        if (token.Length != 64) return false;
        foreach (var character in token)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        }
        return true;
    }
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

    private sealed record StateSnapshot(AdministratorRecord? Administrator, IReadOnlyList<SessionRecord> Sessions);
}

public sealed record SetupRequest(string UserName, string Password);
public sealed record LoginRequest(string UserName, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record LoginResponse(string AccessToken, int ExpiresInSeconds, bool MustChangePassword);
public sealed record SessionValidation(bool IsValid, bool MustChangePassword);
internal sealed record AdministratorRecord(string UserName, string PasswordHash, DateTimeOffset CreatedAt, bool MustChangePassword = false);
internal sealed record SessionRecord(string TokenHash, string UserName, DateTimeOffset ExpiresAt);
public sealed record AuditEvent(DateTimeOffset OccurredAt, string Action, string Subject);
