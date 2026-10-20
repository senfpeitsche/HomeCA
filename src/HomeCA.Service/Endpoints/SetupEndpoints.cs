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
        
        api.MapPost("/setup/configure", async (ConfigureInstanceRequest request, SetupStateService setupState, HomeCaStorage storage, IOptions<HomeCaStorageOptions> storageOptions, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Hostname))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostname"] = ["Hostname is required."] });
        
            var publicUrl = $"http://{request.Hostname}:{request.Port ?? 5080}";
        
            // Persist PublicUrl so it survives restarts.
            var configPath = storage.GetConfigurationFilePath("public-url.conf");
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
        
        api.MapPost("/setup/activate-tls", async (ActivateTlsRequest request, SetupStateService setupState, CertificateIssuanceService issuance, TlsActivationService tls, RenewalPlanRegistry renewalPlans, ILogger<global::Program> logger, CancellationToken ct) =>
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
                var previousCertificateId = setupState.Current.TlsCertificateId;
        
                // Store hostname and certificate ID in setup state
                await setupState.SetHostnameAsync(request.Hostname, ct);
                await setupState.SetTlsCertificateIdAsync(result.Id, ct);
        
                // Write TLS configuration file (readable by the restart helper)
                var tlsConfig = await tls.InstallCertificateAsync(result.Id, request.Hostname, ct);

                // HomeCA's own certificate must not silently expire: renew it like any other
                // certificate; TlsActivationService installs the replacement after renewal.
                var plans = await renewalPlans.ListAsync(ct);
                foreach (var stale in plans.Where(plan => previousCertificateId is not null && plan.CertificateId == previousCertificateId))
                    await renewalPlans.DeleteAsync(stale.Id, ct);
                await renewalPlans.AddAsync(new CreateRenewalPlanRequest(result.Id, 30), ct);
        
                // Advance setup state
                await setupState.AdvanceAsync(SetupPhase.CaInitialized, ct);
        
                logger.LogInformation("TLS activated for {Hostname}, certificate {CertificateId}. Restart required.", request.Hostname, result.Id);
        
                return Results.Ok(new
                {
                    certificateId = result.Id,
                    hostname = request.Hostname,
                    httpsUrl = tlsConfig.HttpsUrl,
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
        
        api.MapPost("/system/activate-tls", async (TlsActivationService tls, HomeCaStorage storage, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            TlsConfiguration? tlsConfig;
            try
            {
                tlsConfig = await tls.ReadConfigurationAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                logger.LogError(ex, "Failed to read TLS configuration from {Path}", tls.ConfigurationFilePath);
                return Results.Problem("The TLS configuration file could not be read.");
            }
            if (tlsConfig is null)
                return Results.Conflict(new { detail = "TLS configuration was not found. Issue a TLS certificate first." });
            if (string.IsNullOrWhiteSpace(tlsConfig.HttpsUrl) || string.IsNullOrWhiteSpace(tlsConfig.PfxPath))
                return Results.Conflict(new { detail = "The TLS configuration is invalid." });
            if (!File.Exists(tlsConfig.PfxPath))
                return Results.Conflict(new { detail = $"Certificate file was not found: {tlsConfig.PfxPath}" });

            // tls.json files written before HTTP was kept alongside HTTPS lack these values.
            tlsConfig = tlsConfig with
            {
                HttpUrl = tlsConfig.HttpUrl ?? TlsActivationService.DefaultHttpUrl,
                CrlBaseUrl = tlsConfig.CrlBaseUrl ?? (string.IsNullOrWhiteSpace(tlsConfig.Hostname) ? null : $"http://{tlsConfig.Hostname}:5080")
            };
        
            // The unit grants write access only to its drop-in directory; the constrained
            // sudoers rule limits the service to creating this specific file.
            var written = tls.WriteSystemdOverride(tlsConfig);
            if (!written.Success)
            {
                logger.LogError("TLS activation failed: {Error}", written.Error);
                return Results.Problem(written.Error);
            }

            // Keep the persisted value in sync for installations that later
            // remove the systemd override or change transport configuration.
            await File.WriteAllTextAsync(storage.GetConfigurationFilePath("public-url.conf"), tlsConfig.PublicUrl, ct);
        
            logger.LogInformation("TLS activation complete — restarting service via systemctl");
            tls.ScheduleRestart();
        
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
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["The new password must be at least 12 characters long."] });
            if (!await administration.ChangePasswordAsync(token, request, ct)) return Results.Unauthorized();
            await setupState.AdvanceAsync(SetupPhase.Initial, ct);
            return Results.NoContent();
        });
        
        // Authorities
    }
}
