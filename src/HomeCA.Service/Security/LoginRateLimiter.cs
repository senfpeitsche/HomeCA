using System.Collections.Concurrent;

namespace HomeCA.Service.Security;

/// <summary>
/// Simple in-memory rate limiter for login attempts. Tracks failed attempts per IP address
/// and blocks further attempts after a configurable threshold within a sliding window.
/// </summary>
public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, LoginAttemptTracker> _attempts = new();
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Returns true if the given IP is currently blocked from login attempts.</summary>
    public bool IsBlocked(string ipAddress)
    {
        if (!_attempts.TryGetValue(ipAddress, out var tracker)) return false;
        lock (tracker)
        {
            tracker.Prune();
            return tracker.IsLockedOut;
        }
    }

    /// <summary>Records a failed login attempt for the given IP address.</summary>
    public void RecordFailure(string ipAddress)
    {
        var tracker = _attempts.GetOrAdd(ipAddress, _ => new LoginAttemptTracker());
        lock (tracker)
        {
            tracker.RecordFailure();
        }
    }

    /// <summary>Clears the failure history for the given IP address after a successful login.</summary>
    public void RecordSuccess(string ipAddress)
    {
        _attempts.TryRemove(ipAddress, out _);
    }

    private sealed class LoginAttemptTracker
    {
        private readonly List<DateTimeOffset> _failures = [];
        private DateTimeOffset? _lockedUntil;

        public bool IsLockedOut => _lockedUntil.HasValue && _lockedUntil.Value > DateTimeOffset.UtcNow;

        public void RecordFailure()
        {
            Prune();
            _failures.Add(DateTimeOffset.UtcNow);
            if (_failures.Count >= MaxAttempts)
            {
                _lockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
            }
        }

        public void Prune()
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(Window);
            _failures.RemoveAll(f => f < cutoff);
            if (_lockedUntil.HasValue && _lockedUntil.Value <= DateTimeOffset.UtcNow)
            {
                _lockedUntil = null;
                _failures.Clear();
            }
        }
    }
}
