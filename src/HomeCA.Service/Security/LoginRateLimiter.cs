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
    private const int SweepInterval = 256;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private int _attemptsSinceSweep;

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

    /// <summary>
    /// Reserves a login attempt. Attempts still in progress count against the limit, so parallel
    /// requests cannot all pass the check before the first failure is recorded. Every successful
    /// reservation must be followed by <see cref="CompleteAttempt"/>.
    /// </summary>
    public bool TryBeginAttempt(string ipAddress)
    {
        if (Interlocked.Increment(ref _attemptsSinceSweep) % SweepInterval == 0) Sweep();
        var tracker = _attempts.GetOrAdd(ipAddress, _ => new LoginAttemptTracker());
        lock (tracker)
        {
            tracker.Prune();
            if (tracker.IsLockedOut || tracker.FailureCount + tracker.InFlight >= MaxAttempts) return false;
            tracker.InFlight++;
            return true;
        }
    }

    /// <summary>
    /// Ends an attempt reserved by <see cref="TryBeginAttempt"/>. <paramref name="succeeded"/> is null
    /// when the attempt aborted (cancellation, server error) and must count neither way.
    /// </summary>
    public void CompleteAttempt(string ipAddress, bool? succeeded)
    {
        if (!_attempts.TryGetValue(ipAddress, out var tracker)) return;
        lock (tracker)
        {
            tracker.InFlight = Math.Max(0, tracker.InFlight - 1);
            if (succeeded == true) tracker.Reset();
            else if (succeeded == false) tracker.RecordFailure();
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
        if (!_attempts.TryGetValue(ipAddress, out var tracker)) return;
        lock (tracker) tracker.Reset();
    }

    /// <summary>Drops idle trackers so addresses that failed once do not accumulate forever.</summary>
    internal void Sweep()
    {
        foreach (var pair in _attempts)
        {
            lock (pair.Value)
            {
                pair.Value.Prune();
                if (pair.Value.IsIdle) _attempts.TryRemove(pair);
            }
        }
    }

    internal int TrackedAddressCount => _attempts.Count;

    private sealed class LoginAttemptTracker
    {
        private readonly List<DateTimeOffset> _failures = [];
        private DateTimeOffset? _lockedUntil;

        public int InFlight { get; set; }
        public int FailureCount => _failures.Count;
        public bool IsLockedOut => _lockedUntil.HasValue && _lockedUntil.Value > DateTimeOffset.UtcNow;
        public bool IsIdle => InFlight == 0 && _failures.Count == 0 && !IsLockedOut;

        public void RecordFailure()
        {
            Prune();
            _failures.Add(DateTimeOffset.UtcNow);
            if (_failures.Count >= MaxAttempts)
            {
                _lockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
            }
        }

        public void Reset()
        {
            _failures.Clear();
            _lockedUntil = null;
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
