using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HomeCA.Service.Security;

/// <summary>
/// Keeps an opaque, browser-session-scoped reference to an authenticated API session.
/// The bearer token stays server-side and is never written to browser storage.
/// </summary>
public sealed class BrowserSessionService
{
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();

    public string Create(string accessToken, int expiresInSeconds)
    {
        RemoveExpired();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[id] = new BrowserSession(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds));
        return id;
    }

    public bool TryGetToken(string? id, out string? accessToken)
    {
        accessToken = null;
        if (string.IsNullOrWhiteSpace(id) || !_sessions.TryGetValue(id, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (!string.IsNullOrWhiteSpace(id)) _sessions.TryRemove(id, out _);
            return false;
        }

        accessToken = session.AccessToken;
        return true;
    }

    public void Remove(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _sessions.TryRemove(id, out _);
    }

    private void RemoveExpired()
    {
        foreach (var session in _sessions.Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow))
            _sessions.TryRemove(session.Key, out _);
    }

    private sealed record BrowserSession(string AccessToken, DateTimeOffset ExpiresAt);
}
