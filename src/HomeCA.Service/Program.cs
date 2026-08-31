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
using HomeCA.Service.Components;
using System.Reflection;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<HomeCaStorageOptions>()
    .Bind(builder.Configuration.GetSection(HomeCaStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<HomeCaStorage>();
builder.Services.AddSingleton<SetupStateService>();
builder.Services.AddSingleton<LocalAdministrationService>();
builder.Services.AddSingleton<CertificateAuthorityService>();
builder.Services.AddSingleton<CertificateIssuanceService>();
builder.Services.AddSingleton<SshCertificateService>();
builder.Services.AddSingleton<DomainRegistry>();
builder.Services.AddSingleton<TargetProfileRegistry>();
builder.Services.AddSingleton<DeploymentPackageService>();
builder.Services.AddSingleton<RenewalPlanRegistry>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IDnsConnector, TechnitiumDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, HetznerDnsConnector>();
builder.Services.AddSingleton<ConnectorCatalog>();
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<InternalAcmeService>();
builder.Services.AddSingleton<ExternalAcmeIssuerRegistry>();
builder.Services.AddSingleton<ExternalAcmeService>();
builder.Services.AddSingleton<CertificateExpiryService>();
builder.Services.AddSingleton<RevocationRegistry>();
builder.Services.AddSingleton<CrlService>();
builder.Services.AddSingleton<BearerTokenFilter>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddScoped<UiStrings>();
builder.Services.AddHostedService<RenewalBackgroundService>();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "HomeCA API";
        document.Info.Version = "v1";
        document.Info.Description = "Self-hosted homelab PKI — manage CAs, issue TLS/SSH certificates, handle ACME flows, and distribute trust anchors.";
        return Task.CompletedTask;
    });
});
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapOpenApi();

// ── Ensure default administrator exists ─────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var administration = scope.ServiceProvider.GetRequiredService<LocalAdministrationService>();
    await administration.EnsureDefaultAdministratorAsync(CancellationToken.None);
}

// ── Public endpoints (no authentication) ────────────────────────────────────

app.MapHealthChecks("/health");
app.MapGet("/api/v1/version", () =>
{
    var assembly = Assembly.GetEntryAssembly()!;
    var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var version = informational.Split('+')[0];
    var commit = informational.Contains('+') ? informational.Split('+')[1] : null;
    return Results.Ok(new { version, commit, runtime = Environment.Version.ToString() });
});
app.MapGet("/api/v1/instance", (HomeCaStorage storage) =>
{
    var informational = Assembly.GetEntryAssembly()!
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var version = informational.Split('+')[0];
    return Results.Ok(new { version, instance = storage.Describe() });
});
app.MapGet("/api/v1/trust-anchor", async (CertificateAuthorityService authorities, CancellationToken ct) =>
{
    var info = await authorities.GetTrustAnchorInfoAsync(ct);
    return info is null ? Results.NotFound(new { detail = "No active root CA found. Initialize the PKI first." }) : Results.Ok(info);
});
app.MapGet("/api/v1/trust-anchor/pem", async (CertificateAuthorityService authorities, CancellationToken ct) =>
{
    var export = await authorities.GetTrustAnchorAsync("pem", ct);
    return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
});
app.MapGet("/api/v1/trust-anchor/der", async (CertificateAuthorityService authorities, CancellationToken ct) =>
{
    var export = await authorities.GetTrustAnchorAsync("der", ct);
    return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
});
app.MapGet("/api/v1/crl/latest", async (CrlService crl, CancellationToken ct) =>
{
    var export = await crl.GetLatestAsync(ct);
    return export is null ? Results.NotFound(new { detail = "No CRL has been generated yet. Generate one first via POST /api/v1/crl." }) : Results.File(export.Content, "application/pkix-crl", export.FileName);
});

// ── Unauthenticated management endpoints ────────────────────────────────────

app.MapPost("/api/v1/setup", async (SetupRequest request, HttpContext context, LocalAdministrationService administration, CancellationToken ct) =>
{
    if (context.Connection.RemoteIpAddress is not null && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
        return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.UserName) || request.Password.Length < 16)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = ["Username is required and password must be at least 16 characters."] });
    return await administration.SetupAsync(request, ct) ? Results.NoContent() : Results.Conflict();
});
app.MapPost("/api/v1/login", async (LoginRequest request, HttpContext context, LocalAdministrationService administration, LoginRateLimiter rateLimiter, CancellationToken ct) =>
{
    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (rateLimiter.IsBlocked(ip))
        return Results.Problem("Too many failed login attempts. Try again later.", statusCode: 429);
    var loginResponse = await administration.LoginAsync(request, ct);
    if (loginResponse is null) { rateLimiter.RecordFailure(ip); return Results.Unauthorized(); }
    rateLimiter.RecordSuccess(ip);
    return Results.Ok(new { accessToken = loginResponse.AccessToken, expiresInSeconds = loginResponse.ExpiresInSeconds, mustChangePassword = loginResponse.MustChangePassword });
});

// ── Unauthenticated ACME client endpoints ───────────────────────────────────

app.MapGet("/api/v1/acme/directory", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.GetDirectoryAsync(ct)));
app.MapPost("/api/v1/acme/accounts", async (RegisterAcmeAccountRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.RegisterAccountAsync(request, ct)));
app.MapGet("/api/v1/connectors", (ConnectorCatalog catalog) => Results.Ok(catalog.Types));

// ── Authenticated endpoints ─────────────────────────────────────────────────

var api = app.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();

// ── Setup wizard endpoints (authenticated) ──────────────────────────────────

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

api.MapPost("/setup/activate-tls", async (ActivateTlsRequest request, SetupStateService setupState, CertificateIssuanceService issuance, HomeCaStorage storage, ILogger<Program> logger, CancellationToken ct) =>
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
api.MapPost("/authorities/initialize", async (CertificateAuthorityService authorities, SetupStateService setupState, CancellationToken ct) =>
{
    var result = await authorities.InitializeAsync(ct);
    await setupState.AdvanceAsync(SetupPhase.PasswordChanged, ct);
    return Results.Ok(result);
});
api.MapGet("/authorities", async (CertificateAuthorityService authorities, CancellationToken ct) => Results.Ok(await authorities.ListAsync(ct)));
api.MapPost("/authorities", async (CreateAuthorityRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { return Results.Ok(await authorities.CreateAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["authority"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPut("/authorities/{id}", async (string id, UpdateAuthorityRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { var authority = await authorities.UpdateAsync(id, request, ct); return authority is null ? Results.NotFound() : Results.Ok(authority); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPost("/authorities/{id}/revoke", async (string id, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { var authority = await authorities.RevokeAsync(id, ct); return authority is null ? Results.NotFound() : Results.Ok(authority); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapDelete("/authorities/{id}", async (string id, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { return await authorities.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapGet("/authorities/{id}/certificate", async (string id, string format, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { var export = await authorities.ExportCertificateAsync(id, format, ct); return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [ex.Message] }); }
});

// Certificates
api.MapGet("/certificates", async (HttpRequest httpRequest, CertificateIssuanceService certificates, CancellationToken ct) =>
{
    var search = httpRequest.Query["search"].FirstOrDefault();
    var skip = int.TryParse(httpRequest.Query["skip"], out var s) ? s : 0;
    var take = int.TryParse(httpRequest.Query["take"], out var t) ? t : 100;
    return Results.Ok(await certificates.ListAsync(ct, search, skip, take));
});
api.MapGet("/certificates/{id}", async (string id, CertificateIssuanceService certificates, CancellationToken ct) =>
{
    var details = await certificates.GetDetailsAsync(id, ct);
    return details is null ? Results.NotFound() : Results.Ok(details);
});
api.MapPost("/certificates", async (IssueCertificateRequest request, CertificateIssuanceService certificates, ILogger<Program> logger, CancellationToken ct) =>
{
    try { return Results.Ok(await certificates.IssueAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["certificate"] = [ex.Message] }); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Certificate issuance failed");
        throw;
    }
});
api.MapDelete("/certificates/{id}", async (string id, HttpRequest httpRequest, CertificateIssuanceService certificates, CancellationToken ct) =>
{
    var reason = httpRequest.Query["reason"].FirstOrDefault() ?? "unspecified";
    return await certificates.RevokeAndDeleteAsync(id, reason, ct) ? Results.NoContent() : Results.NotFound();
});
api.MapGet("/certificates/{id}/export/pem", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "certificate.pem");
    return !File.Exists(path) ? Results.NotFound() : Results.File(path, "application/x-pem-file", $"{id}.pem");
});
api.MapGet("/certificates/{id}/export/chain", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "chain.pem");
    return !File.Exists(path) ? Results.NotFound() : Results.File(path, "application/x-pem-file", $"{id}-chain.pem");
});
api.MapGet("/certificates/{id}/export/key", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "key.pem");
    return !File.Exists(path) ? Results.NotFound() : Results.File(path, "application/x-pem-file", $"{id}-key.pem");
});
api.MapGet("/certificates/{id}/export/fullchain", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "fullchain.pem");
    return !File.Exists(path) ? Results.NotFound() : Results.File(path, "application/x-pem-file", $"{id}-fullchain.pem");
});
api.MapGet("/certificates/{id}/export/bundle", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "bundle.pem");
    return !File.Exists(path) ? Results.NotFound() : Results.File(path, "application/x-pem-file", $"{id}-bundle.pem");
});
api.MapPost("/certificates/{id}/export/pfx", async (string id, PfxExportRequest request, HomeCaStorage storage) =>
{
    var pfxPath = Path.Combine(storage.RootPath, "certificates", id, "certificate.pfx");
    if (!File.Exists(pfxPath)) return Results.NotFound();
    using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
    var bytes = certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, request.Password);
    return Results.File(bytes, "application/x-pkcs12", $"{id}.pfx");
});

// SSH
api.MapGet("/ssh-certificates", async (SshCertificateService certificates, CancellationToken ct) => Results.Ok(await certificates.ListAsync(ct)));
api.MapGet("/ssh-certificates/{id}/content", async (string id, SshCertificateService certificates, CancellationToken ct) =>
{
    var content = await certificates.GetCertificateContentAsync(id, ct);
    return content is null ? Results.NotFound() : Results.Text(content, "text/plain");
});
api.MapDelete("/ssh-certificates/{id}", async (string id, SshCertificateService certificates, CancellationToken ct) =>
    await certificates.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
api.MapPost("/ssh-certificates", async (SshIssueRequest request, SshCertificateService certificates, ILogger<Program> logger, CancellationToken ct) =>
{
    try { return Results.Ok(await certificates.IssueAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["ssh"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "SSH certificate issuance failed");
        throw;
    }
});
api.MapGet("/ssh-ca-keys/{kind}", async (string kind, SshCertificateService certificates, CancellationToken ct) =>
{
    var key = await certificates.GetCaPublicKeyAsync(kind, ct);
    return key is null ? Results.NotFound(new { detail = "SSH CA key not found. Initialize certificate authorities first." }) : Results.Ok(key);
});

// Domains
api.MapGet("/domains", async (DomainRegistry domains, CancellationToken ct) => Results.Ok(await domains.ListAsync(ct)));
api.MapPost("/domains", async (CreateDomainRequest request, DomainRegistry domains, CancellationToken ct) => Results.Ok(await domains.AddAsync(request, ct)));
api.MapPut("/domains/{name}", async (string name, CreateDomainRequest request, DomainRegistry domains, CancellationToken ct) =>
{
    try { var domain = await domains.UpdateAsync(name, request, ct); return domain is null ? Results.NotFound() : Results.Ok(domain); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapDelete("/domains/{name}", async (string name, DomainRegistry domains, CancellationToken ct) => await domains.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound());

// Profiles
api.MapGet("/profiles", async (TargetProfileRegistry profiles, CancellationToken ct) => Results.Ok(await profiles.ListAsync(ct)));
api.MapPost("/profiles", async (CreateTargetProfileRequest request, TargetProfileRegistry profiles, CancellationToken ct) =>
{
    try { return Results.Ok(await profiles.AddAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPut("/profiles/{id}", async (string id, UpdateTargetProfileRequest request, TargetProfileRegistry profiles, CancellationToken ct) =>
{
    try { var profile = await profiles.UpdateAsync(id, request, ct); return profile is null ? Results.NotFound() : Results.Ok(profile); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [ex.Message] }); }
});
api.MapDelete("/profiles/{id}", async (string id, TargetProfileRegistry profiles, CancellationToken ct) =>
{
    try { return await profiles.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});

// Connectors
api.MapGet("/connector-instances", async (ConnectorRegistry connectors, CancellationToken ct) => Results.Ok((await connectors.ListAsync(ct)).Select(c => new { c.Id, c.Name, c.Type, c.CreatedAt })));
api.MapPost("/connector-instances", async (CreateConnectorRequest request, ConnectorRegistry connectors, CancellationToken ct) =>
{
    try { return Results.Ok(await connectors.AddAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connector"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPut("/connector-instances/{id}", async (string id, CreateConnectorRequest request, ConnectorRegistry connectors, CancellationToken ct) =>
{
    try { var connector = await connectors.UpdateAsync(id, request, ct); return connector is null ? Results.NotFound() : Results.Ok(connector); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connector"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPost("/connector-instances/{id}/check", async (string id, ConnectorRegistry registry, ConnectorCatalog catalog, ILogger<Program> logger, CancellationToken ct) =>
{
    var connector = await registry.GetAsync(id, ct);
    var implementation = connector is null ? null : catalog.Find(connector.Type);
    if (connector is null || implementation is null) return Results.NotFound();
    try { return Results.Ok(await implementation.CheckAsync(new ConnectorSettings(connector.Name, connector.Type, connector.Secrets), ct)); }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Connector check failed for {ConnectorId} ({Type})", id, connector.Type);
        return Results.Ok(new ConnectorCheckResult(false, [], "The connector could not be reached. Check its settings and network access."));
    }
});
api.MapPost("/connector-instances/{id}/txt-test", async (string id, HttpRequest request, ConnectorRegistry registry, ConnectorCatalog catalog, CancellationToken ct) =>
{
    var connector = await registry.GetAsync(id, ct);
    var implementation = connector is null ? null : catalog.Find(connector.Type);
    if (connector is null || implementation is null) return Results.NotFound();
    var domain = request.Query["domain"].ToString();
    if (string.IsNullOrWhiteSpace(domain)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = ["A domain query parameter is required."] });
    var name = $"_homeca-test.{domain}".TrimEnd('.');
    var value = Guid.NewGuid().ToString("N");
    var settings = new ConnectorSettings(connector.Name, connector.Type, connector.Secrets);
    await implementation.UpsertTxtRecordAsync(settings, name, value, ct);
    await implementation.DeleteTxtRecordAsync(settings, name, value, ct);
    return Results.NoContent();
});
api.MapDelete("/connector-instances/{id}", async (string id, ConnectorRegistry connectors, CancellationToken ct) => await connectors.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// ACME (authenticated)
api.MapGet("/acme/accounts", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.ListAccountsAsync(ct)));
api.MapGet("/acme/orders", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.ListOrdersAsync(ct)));
api.MapPost("/acme/orders", async (AcmeOrderRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.CreateOrderAsync(request.AccountId, request.Identifiers, ct)));
api.MapGet("/acme/orders/{orderId}", async (string orderId, InternalAcmeService acme, CancellationToken ct) =>
{
    var order = await acme.GetOrderAsync(orderId, ct);
    return order is null ? Results.NotFound() : Results.Ok(order);
});
api.MapPost("/acme/orders/{orderId}/finalize", async (string orderId, FinalizeAcmeOrderRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.FinalizeOrderAsync(orderId, request, ct)));
api.MapGet("/acme/external-issuers", async (ExternalAcmeIssuerRegistry issuers, CancellationToken ct) => Results.Ok(await issuers.ListAsync(ct)));
api.MapPost("/acme/external-issuers", async (CreateExternalAcmeIssuerRequest request, ExternalAcmeIssuerRegistry issuers, CancellationToken ct) => Results.Ok(await issuers.AddAsync(request, ct)));
api.MapPut("/acme/external-issuers/{id}", async (string id, CreateExternalAcmeIssuerRequest request, ExternalAcmeIssuerRegistry issuers, CancellationToken ct) =>
{
    var updated = await issuers.UpdateAsync(id, request, ct);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});
api.MapDelete("/acme/external-issuers/{id}", async (string id, ExternalAcmeIssuerRegistry issuers, CancellationToken ct) => await issuers.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
api.MapPost("/acme/external-orders", async (ExternalAcmeOrderRequest request, ExternalAcmeService externalAcme, CancellationToken ct) =>
{
    try { return Results.Ok(await externalAcme.RequestCertificateAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapGet("/acme/external-certificates", async (ExternalAcmeService externalAcme, CancellationToken ct) => Results.Ok(await externalAcme.ListCertificatesAsync(ct)));

// Renewal plans
api.MapGet("/renewal-plans", async (RenewalPlanRegistry plans, CancellationToken ct) => Results.Ok(await plans.ListAsync(ct)));
api.MapPost("/renewal-plans", async (CreateRenewalPlanRequest body, RenewalPlanRegistry plans, CancellationToken ct) => Results.Ok(await plans.AddAsync(body, ct)));
api.MapPut("/renewal-plans/{id}", async (string id, UpdateRenewalPlanRequest body, RenewalPlanRegistry plans, CancellationToken ct) =>
{
    var plan = await plans.UpdateAsync(id, body, ct);
    return plan is null ? Results.NotFound() : Results.Ok(plan);
});
api.MapDelete("/renewal-plans/{id}", async (string id, RenewalPlanRegistry plans, CancellationToken ct) => await plans.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// Operations
api.MapGet("/warnings/expiring", (CertificateExpiryService expiry) => Results.Ok(expiry.GetWarnings()));
api.MapGet("/revocations", async (RevocationRegistry registry, CancellationToken ct) => Results.Ok(await registry.ListAsync(ct)));
api.MapPost("/revocations/{serial}/{reason}", async (string serial, string reason, RevocationRegistry registry, CancellationToken ct) => Results.Ok(await registry.RevokeAsync(serial, reason, ct)));
api.MapPost("/crl", async (CrlService crl, ILogger<Program> logger, CancellationToken ct) =>
{
    try { return Results.Ok(new { path = await crl.GenerateAsync(ct) }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "CRL generation failed");
        throw;
    }
});

// Backups
api.MapPost("/backups", async (HomeCaStorage storage, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var backup = await storage.CreateBackupAsync(ct);
        return Results.Created($"/api/v1/backups/{backup.FileName}", backup);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Backup creation failed");
        throw;
    }
});
api.MapPost("/backups/{fileName}/verify", async (string fileName, HomeCaStorage storage, CancellationToken ct) => Results.Ok(await storage.VerifyBackupAsync(fileName, ct)));

// Audit
api.MapGet("/audit", async (HttpRequest request, LocalAdministrationService administration, CancellationToken ct) =>
{
    var skip = int.TryParse(request.Query["skip"], out var s) ? s : 0;
    var take = int.TryParse(request.Query["take"], out var t) ? t : 100;
    var action = request.Query["action"].FirstOrDefault();
    return Results.Ok(await administration.ReadAuditLogAsync(skip, take, action, ct));
});

// ── Blazor UI ───────────────────────────────────────────────────────────────

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
