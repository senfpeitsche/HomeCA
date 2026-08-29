using System.Security.Cryptography;
using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Security;

public sealed class LocalAdministrationService(HomeCaStorage storage)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _adminPath = Path.Combine(storage.RootPath, "state", "administrator.json");
    private readonly string _sessionPath = Path.Combine(storage.RootPath, "state", "sessions.json");
    private readonly string _auditPath = Path.Combine(storage.RootPath, "audit", "events.ndjson");

    public async Task<bool> SetupAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_adminPath)) return false;
            var administrator = new AdministratorRecord(request.UserName, PasswordHasher.Hash(request.Password), DateTimeOffset.UtcNow);
            await WriteJsonAsync(_adminPath, administrator, cancellationToken);
            await AuditAsync("administrator.setup", request.UserName, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_adminPath)) return null;
            var administrator = await ReadJsonAsync<AdministratorRecord>(_adminPath, cancellationToken);
            if (administrator is null || !string.Equals(administrator.UserName, request.UserName, StringComparison.Ordinal) || !PasswordHasher.Verify(request.Password, administrator.PasswordHash))
            {
                await AuditAsync("administrator.login_failed", request.UserName, cancellationToken);
                return null;
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var sessions = await ReadJsonAsync<List<SessionRecord>>(_sessionPath, cancellationToken) ?? [];
            sessions.RemoveAll(session => session.ExpiresAt <= DateTimeOffset.UtcNow);
            sessions.Add(new SessionRecord(TokenHash(token), administrator.UserName, DateTimeOffset.UtcNow.AddHours(12)));
            await WriteJsonAsync(_sessionPath, sessions, cancellationToken);
            await AuditAsync("administrator.login", administrator.UserName, cancellationToken);
            return token;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsSessionValidAsync(string? token, CancellationToken cancellationToken)
    {
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

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) => File.Exists(path) ? await JsonSerializer.DeserializeAsync<T>(File.OpenRead(path), cancellationToken: cancellationToken) : default;
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath)) await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}

public sealed record SetupRequest(string UserName, string Password);
public sealed record LoginRequest(string UserName, string Password);
internal sealed record AdministratorRecord(string UserName, string PasswordHash, DateTimeOffset CreatedAt);
internal sealed record SessionRecord(string TokenHash, string UserName, DateTimeOffset ExpiresAt);
