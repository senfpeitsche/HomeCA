using System.Diagnostics;
using System.Text.Json;

namespace HomeCA.Service.Infrastructure;

/// <summary>
/// Owns HomeCA's own HTTPS configuration: the stable certificate location, tls.json and the systemd
/// drop-in that switches Kestrel to HTTPS. Setup and automatic renewal of the HomeCA certificate both
/// go through this service so a renewed certificate actually replaces the one Kestrel serves.
/// </summary>
public sealed class TlsActivationService
{
    public const string DefaultHttpUrl = "http://0.0.0.0:5080";
    public const string DefaultHttpsUrl = "https://0.0.0.0:5443";
    private const string OverrideDirectory = "/etc/systemd/system/homeca.service.d";
    private const string OverridePath = "/etc/systemd/system/homeca.service.d/tls.conf";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly HomeCaStorage _storage;
    private readonly SetupStateService _setupState;
    private readonly ILogger<TlsActivationService> _logger;
    private readonly Func<string, string, string?, CommandResult> _runCommand;
    private readonly Func<string?> _currentUrls;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TlsActivationService(HomeCaStorage storage, SetupStateService setupState, ILogger<TlsActivationService> logger)
        : this(storage, setupState, logger, RunProcess, () => Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    {
    }

    internal TlsActivationService(HomeCaStorage storage, SetupStateService setupState, ILogger<TlsActivationService> logger,
        Func<string, string, string?, CommandResult> runCommand, Func<string?> currentUrls)
    {
        _storage = storage;
        _setupState = setupState;
        _logger = logger;
        _runCommand = runCommand;
        _currentUrls = currentUrls;
    }

    public string ConfigurationFilePath => _storage.GetConfigurationFilePath("tls.json");
    public string PfxPath => Path.Combine(_storage.ConfigurationPath, "tls", "homeca-tls.pfx");
    public string ChainPath => Path.Combine(_storage.ConfigurationPath, "tls", "chain.pem");

    /// <summary>Whether this process currently serves HTTPS itself.</summary>
    public bool IsHttpsActive => _currentUrls()?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Copies an issued certificate to the stable TLS location and writes tls.json. The stable path
    /// lets a renewal replace the certificate without changing the systemd drop-in.
    /// </summary>
    public async Task<TlsConfiguration> InstallCertificateAsync(string certificateId, string hostname, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PfxPath)!);
            CopyAtomically(Path.Combine(_storage.RootPath, "certificates", certificateId, "certificate.pfx"), PfxPath);
            CopyAtomically(Path.Combine(_storage.RootPath, "exports", certificateId, "chain.pem"), ChainPath);

            var configuration = new TlsConfiguration(
                DefaultHttpsUrl,
                PfxPath,
                $"https://{hostname}:5443",
                hostname,
                DefaultHttpUrl,
                // CRLs stay on plain HTTP: relying parties fetch them before they trust the HTTPS endpoint.
                $"http://{hostname}:5080");
            var temporaryPath = ConfigurationFilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions), ct);
            File.Move(temporaryPath, ConfigurationFilePath, true);
            _logger.LogInformation("Installed certificate {CertificateId} as HomeCA TLS certificate for {Hostname}", certificateId, hostname);
            return configuration;
        }
        finally { _gate.Release(); }
    }

    public async Task<TlsConfiguration?> ReadConfigurationAsync(CancellationToken ct)
    {
        if (!File.Exists(ConfigurationFilePath)) return null;
        return JsonSerializer.Deserialize<TlsConfiguration>(await File.ReadAllTextAsync(ConfigurationFilePath, ct), JsonOptions);
    }

    /// <summary>Builds the systemd drop-in. HTTP stays enabled for CRL distribution, trust bootstrap and ACME clients.</summary>
    public static string BuildSystemdOverride(TlsConfiguration configuration)
    {
        var urls = string.IsNullOrWhiteSpace(configuration.HttpUrl)
            ? configuration.HttpsUrl
            : $"{configuration.HttpUrl};{configuration.HttpsUrl}";
        var lines = new List<string>
        {
            "[Service]",
            $"Environment=ASPNETCORE_URLS={urls}",
            $"Environment=ASPNETCORE_Kestrel__Certificates__Default__Path={configuration.PfxPath}",
            $"Environment=Storage__PublicUrl={configuration.PublicUrl}"
        };
        if (!string.IsNullOrWhiteSpace(configuration.CrlBaseUrl))
            lines.Add($"Environment=Storage__CrlBaseUrl={configuration.CrlBaseUrl}");
        lines.Add("");
        return string.Join('\n', lines);
    }

    /// <summary>Writes the systemd drop-in and reloads systemd. The caller decides when to restart.</summary>
    public CommandResult WriteSystemdOverride(TlsConfiguration configuration)
    {
        var mkdir = _runCommand("sudo", $"mkdir -p {OverrideDirectory}", null);
        if (!mkdir.Success) return mkdir with { Error = $"The systemd override directory could not be created: {mkdir.Error}" };
        var tee = _runCommand("sudo", $"tee {OverridePath}", BuildSystemdOverride(configuration));
        if (!tee.Success) return tee with { Error = $"The systemd override could not be written: {tee.Error}" };
        var reload = _runCommand("sudo", "systemctl daemon-reload", null);
        if (!reload.Success) return reload with { Error = $"systemctl daemon-reload failed: {reload.Error}" };
        _logger.LogInformation("Wrote systemd TLS override to {Path}", OverridePath);
        return reload;
    }

    /// <summary>Restarts the service shortly after returning so a pending HTTP response can still be sent.</summary>
    public void ScheduleRestart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            var restart = _runCommand("sudo", "systemctl restart homeca", null);
            if (!restart.Success) _logger.LogError("Restarting HomeCA failed: {Error}", restart.Error);
        });
    }

    /// <summary>
    /// Called after a certificate was renewed. When it is HomeCA's own TLS certificate, the replacement is
    /// installed and, if HTTPS is active, the drop-in is refreshed and the service restarted to load it.
    /// </summary>
    public async Task<bool> OnCertificateRenewedAsync(string previousCertificateId, string renewedCertificateId, CancellationToken ct)
    {
        var state = _setupState.Current;
        if (!string.Equals(state.TlsCertificateId, previousCertificateId, StringComparison.OrdinalIgnoreCase)) return false;
        var hostname = state.Hostname ?? (await ReadConfigurationAsync(ct))?.Hostname;
        if (string.IsNullOrWhiteSpace(hostname))
        {
            _logger.LogWarning("Renewed HomeCA TLS certificate {CertificateId}, but no hostname is recorded; install it manually", renewedCertificateId);
            return false;
        }

        var configuration = await InstallCertificateAsync(renewedCertificateId, hostname, ct);
        await _setupState.SetTlsCertificateIdAsync(renewedCertificateId, ct);
        if (!IsHttpsActive) return true;

        // Installations activated before the stable path existed reference the old PFX in the drop-in.
        var result = WriteSystemdOverride(configuration);
        if (!result.Success)
        {
            _logger.LogError("Renewed HomeCA TLS certificate {CertificateId} could not be activated: {Error}", renewedCertificateId, result.Error);
            return true;
        }
        _logger.LogInformation("Restarting HomeCA to serve the renewed TLS certificate {CertificateId}", renewedCertificateId);
        ScheduleRestart();
        return true;
    }

    private static void CopyAtomically(string source, string target)
    {
        var temporaryPath = target + ".tmp";
        File.Copy(source, temporaryPath, true);
        File.Move(temporaryPath, target, true);
    }

    private static CommandResult RunProcess(string fileName, string arguments, string? stdin)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = "/"
        };
        process.Start();
        if (stdin is not null) { process.StandardInput.Write(stdin); process.StandardInput.Close(); }
        // Read both streams concurrently; reading them one after another can deadlock on a full pipe.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(true);
            return new CommandResult(false, "", $"{fileName} {arguments} timed out.");
        }
        return new CommandResult(process.ExitCode == 0, output.Result.Trim(), error.Result.Trim());
    }
}

public sealed record TlsConfiguration(string HttpsUrl, string PfxPath, string PublicUrl, string Hostname, string? HttpUrl = null, string? CrlBaseUrl = null);
public sealed record CommandResult(bool Success, string Output, string Error);
