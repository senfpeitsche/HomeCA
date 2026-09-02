using HomeCA.Service.Infrastructure;
using HomeCA.Service.Security;
using HomeCA.Service.Pki;
using HomeCA.Service.Domains;
using HomeCA.Service.Profiles;
using HomeCA.Service.Connectors;
using HomeCA.Service.Acme;
using HomeCA.Service.Operations;
using HomeCA.Service.Revocation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Automation;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Endpoints;
 static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapGet("/setup/state", (SetupStateService setupState) => Results.Ok(new
        {
            phase = setupState.Current.SetupPhase.ToString().ToLowerInvariant(),
            isComplete = setupState.IsSetupComplete,
            hostname = setupState.Current.Hostname,
            tlsCertificateId = setupState.Current.TlsCertificateId
        }));
        
        api.MapPost("/setup/skip", async (SetupStateService setupState, CancellationToken ct) =>
        {
            var state = await setupState.SkipWizardAsync(ct);
            return Results.Ok(new { phase = state.SetupPhase.ToString().ToLowerInvariant(), isComplete = true });
        });
        
        api.MapPost("/setup/configure", async (ConfigureInstanceRequest request, SetupStateService setupState, IOptions<HomeCaStorageOptions> storageOptions, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Hostname))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostname"] = ["Hostname is required."] });
        
            var publicUrl = $"http://{request.Hostname}:{request.Port ?? 5080}";
        
            // Persist PublicUrl so it survives restarts — write to /etc/homeca/
            var configPath = "/etc/homeca/public-url.conf";
            try
            {
                await File.WriteAllTextAsync(configPath, publicUrl, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not write PublicUrl to {Path} — will use in-memory value only", configPath);
            }
        
            // Also update the in-memory options for the current process
            storageOptions.Value.PublicUrl = publicUrl;
        
            // Store hostname in setup state
            await setupState.SetHostnameAsync(request.Hostname, ct);
            logger.LogInformation("Instance configured: hostname={Hostname}, publicUrl={PublicUrl}", request.Hostname, publicUrl);
        
            return Results.Ok(new { hostname = request.Hostname, publicUrl });
        });
        
        api.MapPost("/setup/activate-tls", async (ActivateTlsRequest request, SetupStateService setupState, CertificateIssuanceService issuance, HomeCaStorage storage, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Hostname))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostname"] = ["Hostname is required."] });
        
            try
            {
                // Collect SANs: hostname + optional IPs
                var dnsNames = new List<string> { request.Hostname };
                var ipAddresses = new List<string>();
                if (!string.IsNullOrWhiteSpace(request.IpAddress))
                    ipAddresses.Add(request.IpAddress);
        
                // Issue TLS certificate for this HomeCA instance
                var issueRequest = new IssueCertificateRequest("TLS", dnsNames, ipAddresses, 365, "ECC");
                var result = await issuance.IssueAsync(issueRequest, ct);
        
                // Store hostname and certificate ID in setup state
                await setupState.SetHostnameAsync(request.Hostname, ct);
                await setupState.SetTlsCertificateIdAsync(result.Id, ct);
        
                // Write TLS configuration file (readable by the restart helper)
                var pfxPath = Path.Combine(storage.RootPath, "certificates", result.Id, "certificate.pfx");
                var httpsUrl = "https://0.0.0.0:5443";
                var tlsConfig = new
                {
                    httpsUrl,
                    pfxPath,
                    publicUrl = $"https://{request.Hostname}:5443",
                    hostname = request.Hostname
                };
                var tlsConfigPath = Path.Combine(Path.GetDirectoryName(storage.RootPath)!, "..", "etc", "homeca", "tls.json");
                // Normalize: /etc/homeca/tls.json
                tlsConfigPath = "/etc/homeca/tls.json";
                await File.WriteAllTextAsync(tlsConfigPath, System.Text.Json.JsonSerializer.Serialize(tlsConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
        
                // Advance setup state
                await setupState.AdvanceAsync(SetupPhase.CaInitialized, ct);
        
                logger.LogInformation("TLS activated for {Hostname}, certificate {CertificateId}. Restart required.", request.Hostname, result.Id);
        
                return Results.Ok(new
                {
                    certificateId = result.Id,
                    hostname = request.Hostname,
                    httpsUrl,
                    message = "TLS configured. Restart the HomeCA service to apply: systemctl restart homeca"
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Conflict(new { detail = ex.Message });
            }
        });
        
        api.MapPost("/setup/complete", async (SetupStateService setupState, CancellationToken ct) =>
        {
            await setupState.AdvanceAsync(SetupPhase.TlsConfigured, ct);
            return Results.Ok(new { phase = "complete", isComplete = true });
        });
        
        api.MapPost("/system/activate-tls", async (SetupStateService setupState, HomeCaStorage storage, IHostApplicationLifetime lifetime, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            // Verify that TLS configuration has been generated
            const string tlsConfigPath = "/etc/homeca/tls.json";
            if (!File.Exists(tlsConfigPath))
                return Results.Conflict(new { detail = "TLS-Konfiguration nicht gefunden. Bitte zuerst ein TLS-Zertifikat ausstellen." });
        
            string tlsJson;
            try
            {
                tlsJson = await File.ReadAllTextAsync(tlsConfigPath, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read TLS configuration from {Path}", tlsConfigPath);
                return Results.Problem("TLS-Konfigurationsdatei konnte nicht gelesen werden.");
            }
        
            var tlsConfig = System.Text.Json.JsonSerializer.Deserialize<TlsConfigDto>(tlsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (tlsConfig is null || string.IsNullOrWhiteSpace(tlsConfig.HttpsUrl) || string.IsNullOrWhiteSpace(tlsConfig.PfxPath))
                return Results.Conflict(new { detail = "TLS-Konfiguration ist ungültig." });
        
            if (!File.Exists(tlsConfig.PfxPath))
                return Results.Conflict(new { detail = $"Zertifikatsdatei nicht gefunden: {tlsConfig.PfxPath}" });
        
            // Write the systemd override that switches Kestrel to HTTPS.
            // The unit grants write access only to its drop-in directory. The constrained
            // sudoers rule below limits the service to creating this specific file.
            const string overrideDir = "/etc/systemd/system/homeca.service.d";
            const string overridePath = "/etc/systemd/system/homeca.service.d/tls.conf";
        
            var mkdirResult = RunProcess("sudo", $"mkdir -p {overrideDir}");
            if (!mkdirResult.Success)
                return Results.Problem($"Systemd-Override-Verzeichnis konnte nicht erstellt werden: {mkdirResult.Error}");
        
            var overrideLines = new[]
            {
                "[Service]",
                $"Environment=ASPNETCORE_URLS={tlsConfig.HttpsUrl}",
                $"Environment=ASPNETCORE_Kestrel__Certificates__Default__Path={tlsConfig.PfxPath}",
                $"Environment=Storage__PublicUrl={tlsConfig.PublicUrl}",
                ""
            };
            var overrideContent = string.Join('\n', overrideLines);
            var teeResult = RunProcess("sudo", $"tee {overridePath}", overrideContent);
            if (!teeResult.Success)
                return Results.Problem($"Systemd-Override konnte nicht geschrieben werden: {teeResult.Error}");
        
            logger.LogInformation("Wrote systemd TLS override to {Path} via sudo", overridePath);
        
            // Reload systemd to pick up the override, then restart the service
            var reload = RunProcess("sudo", "systemctl daemon-reload");
            if (!reload.Success)
            {
                logger.LogError("systemctl daemon-reload failed: {Error}", reload.Error);
                return Results.Problem($"systemctl daemon-reload fehlgeschlagen: {reload.Error}");
            }
        
            logger.LogInformation("TLS activation complete — restarting service via systemctl");
        
            // Fire-and-forget: restart the service. The response must be sent before we die.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // give the HTTP response time to flush
                RunProcess("sudo", "systemctl restart homeca");
            });
        
            return Results.Ok(new
            {
                message = "TLS wird aktiviert. HomeCA startet neu und ist gleich unter HTTPS erreichbar.",
                publicUrl = tlsConfig.PublicUrl
            });
        });
        
        api.MapPost("/setup/reset", async (SetupStateService setupState, CancellationToken ct) =>
        {
            var state = await setupState.ResetAsync(ct);
            return Results.Ok(new { phase = state.SetupPhase.ToString().ToLowerInvariant(), isComplete = false });
        });
        
        api.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext context, LocalAdministrationService administration, SetupStateService setupState, CancellationToken ct) =>
        {
            var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["Das neue Passwort muss mindestens 12 Zeichen lang sein."] });
            if (!await administration.ChangePasswordAsync(token, request, ct)) return Results.Unauthorized();
            await setupState.AdvanceAsync(SetupPhase.Initial, ct);
            return Results.NoContent();
        });
        
        // Authorities
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string? stdin = null)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = "/"
        };
        process.Start();
        if (stdin is not null) { process.StandardInput.Write(stdin); process.StandardInput.Close(); }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));
        return new ProcessResult(process.ExitCode == 0, output.Trim(), error.Trim());
    }

    private sealed record ProcessResult(bool Success, string Output, string Error);
}
