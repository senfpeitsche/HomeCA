using System.Collections.Concurrent;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Output of running guided SSH setups, so the wizard can show it while the install request is still open. Lines are
/// already cleaned of credentials and the target token. Entries live only in memory and expire after half an hour.
/// </summary>
public sealed class SshSetupProgress(TimeProvider? timeProvider = null)
{
    private const int MaximumLines = 2000;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Progress IDs are chosen by the browser; only 32 hex digits are accepted.</summary>
    public static bool IsValidId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    public void Append(string? id, string line)
    {
        if (!IsValidId(id)) return;
        var now = _time.GetUtcNow();
        foreach (var (key, stale) in _entries)
            if (now - stale.Updated > Retention) _entries.TryRemove(key, out _);
        var entry = _entries.GetOrAdd(id!, _ => new Entry());
        lock (entry)
        {
            if (entry.Lines.Count < MaximumLines) entry.Lines.Add($"{now.ToLocalTime():HH:mm:ss} {line}");
            entry.Updated = now;
        }
    }

    public void Complete(string? id)
    {
        if (!IsValidId(id) || !_entries.TryGetValue(id!, out var entry)) return;
        lock (entry) { entry.Done = true; entry.Updated = _time.GetUtcNow(); }
    }

    public SshSetupProgressSnapshot Get(string id, int from)
    {
        if (!_entries.TryGetValue(id, out var entry)) return new([], 0, false);
        lock (entry)
        {
            var start = Math.Clamp(from, 0, entry.Lines.Count);
            return new(entry.Lines.Skip(start).ToList(), entry.Lines.Count, entry.Done);
        }
    }

    private sealed class Entry
    {
        public List<string> Lines { get; } = [];
        public DateTimeOffset Updated { get; set; }
        public bool Done { get; set; }
    }
}

public sealed record SshSetupProgressSnapshot(IReadOnlyList<string> Lines, int Next, bool Done);
