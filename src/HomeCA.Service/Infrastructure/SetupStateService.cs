using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeCA.Service.Infrastructure;

/// <summary>
/// Tracks the post-install wizard progress. The wizard appears after every login
/// until all phases are complete and TLS is active.
/// </summary>
public sealed class SetupStateService
{
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SetupStateService> _logger;
    private SetupState _current;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public SetupStateService(HomeCaStorage storage, ILogger<SetupStateService> logger)
    {
        _statePath = Path.Combine(storage.RootPath, "state", "homeca-state.json");
        _logger = logger;
        _current = Load();
    }

    public SetupState Current => _current;

    public bool IsSetupComplete => _current.SetupPhase == SetupPhase.Complete;

    /// <summary>Advances the setup phase if the given phase is the next expected one.</summary>
    public async Task<SetupState> AdvanceAsync(SetupPhase completedPhase, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Only advance if the completed phase is the current one
            if (_current.SetupPhase == completedPhase)
            {
                var next = completedPhase switch
                {
                    SetupPhase.Initial => SetupPhase.PasswordChanged,
                    SetupPhase.PasswordChanged => SetupPhase.CaInitialized,
                    SetupPhase.CaInitialized => SetupPhase.TlsConfigured,
                    SetupPhase.TlsConfigured => SetupPhase.Complete,
                    _ => SetupPhase.Complete
                };
                _current = _current with { SetupPhase = next };
                await SaveAsync(ct);
                _logger.LogInformation("Setup wizard advanced to phase {Phase}", next);
            }

            return _current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stores the hostname chosen during TLS setup.</summary>
    public async Task<SetupState> SetHostnameAsync(string hostname, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _current = _current with { Hostname = hostname };
            await SaveAsync(ct);
            return _current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stores the TLS certificate ID after issuance.</summary>
    public async Task<SetupState> SetTlsCertificateIdAsync(string certificateId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _current = _current with { TlsCertificateId = certificateId };
            await SaveAsync(ct);
            return _current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Marks the wizard as skipped (user can dismiss it permanently).</summary>
    public async Task<SetupState> SkipWizardAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _current = _current with { SetupPhase = SetupPhase.Complete };
            await SaveAsync(ct);
            _logger.LogInformation("Setup wizard skipped by user");
            return _current;
        }
        finally { _gate.Release(); }
    }

    private SetupState Load()
    {
        if (!File.Exists(_statePath))
            return new SetupState();

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<SetupState>(json, JsonOptions) ?? new SetupState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read setup state from {Path}, starting fresh", _statePath);
            return new SetupState();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var tmp = _statePath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, _current, JsonOptions, ct);
        }
        File.Move(tmp, _statePath, true);
    }
}

public enum SetupPhase
{
    /// <summary>Fresh install — no setup steps completed yet.</summary>
    Initial,
    /// <summary>Admin password has been changed from default.</summary>
    PasswordChanged,
    /// <summary>Root CA and Issuing CA have been created.</summary>
    CaInitialized,
    /// <summary>TLS certificate issued and Kestrel configured — pending restart.</summary>
    TlsConfigured,
    /// <summary>Setup is fully complete.</summary>
    Complete
}

public sealed record SetupState
{
    public int Version { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public SetupPhase SetupPhase { get; init; } = SetupPhase.Initial;
    public string? Hostname { get; init; }
    public string? TlsCertificateId { get; init; }
}

public sealed record ActivateTlsRequest(string Hostname, string? IpAddress = null);
