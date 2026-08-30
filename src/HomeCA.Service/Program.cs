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
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<HomeCaStorageOptions>()
    .Bind(builder.Configuration.GetSection(HomeCaStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<HomeCaStorage>();
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
builder.Services.AddSingleton<CertificateExpiryService>();
builder.Services.AddSingleton<RevocationRegistry>();
builder.Services.AddSingleton<CrlService>();
builder.Services.AddHealthChecks();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHealthChecks("/health");
app.MapGet("/api/v1/instance", (HomeCaStorage storage) => Results.Ok(storage.Describe()));
app.MapPost("/api/v1/setup", async (SetupRequest request, HttpContext context, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
    if (context.Connection.RemoteIpAddress is not null && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
    {
        return Results.Forbid();
    }
    if (string.IsNullOrWhiteSpace(request.UserName) || request.Password.Length < 16)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = ["Username is required and password must be at least 16 characters."] });
    }
    return await administration.SetupAsync(request, cancellationToken) ? Results.NoContent() : Results.Conflict();
});
app.MapPost("/api/v1/login", async (LoginRequest request, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
    var token = await administration.LoginAsync(request, cancellationToken);
    return token is null ? Results.Unauthorized() : Results.Ok(new { accessToken = token, expiresInSeconds = 43200 });
});
app.MapPost("/api/v1/authorities/initialize", async (HttpRequest request, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    return !await administration.IsSessionValidAsync(token, cancellationToken) ? Results.Unauthorized() : Results.Ok(await authorities.InitializeAsync(cancellationToken));
});
app.MapPost("/api/v1/certificates", async (IssueCertificateRequest request, HttpRequest httpRequest, CertificateIssuanceService certificates, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
    var token = httpRequest.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    return !await administration.IsSessionValidAsync(token, cancellationToken) ? Results.Unauthorized() : Results.Ok(await certificates.IssueAsync(request, cancellationToken));
});
app.MapPost("/api/v1/ssh-certificates", async (SshIssueRequest request, HttpRequest httpRequest, SshCertificateService certificates, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
    var token = httpRequest.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    return !await administration.IsSessionValidAsync(token, cancellationToken) ? Results.Unauthorized() : Results.Ok(await certificates.IssueAsync(request, cancellationToken));
});
app.MapGet("/api/v1/domains", async (HttpRequest request, DomainRegistry domains, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await domains.ListAsync(cancellationToken));
});
app.MapPost("/api/v1/domains", async (CreateDomainRequest request, HttpRequest httpRequest, DomainRegistry domains, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await domains.AddAsync(request,cancellationToken));
});
app.MapGet("/api/v1/profiles", async (HttpRequest request, TargetProfileRegistry profiles, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await profiles.ListAsync(cancellationToken));
});
app.MapPost("/api/v1/profiles", async (CreateTargetProfileRequest request, HttpRequest httpRequest, TargetProfileRegistry profiles, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized();
 try { return Results.Ok(await profiles.AddAsync(request,cancellationToken)); }
 catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string,string[]> { ["profile"] = [exception.Message] }); }
 catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
});
app.MapPut("/api/v1/profiles/{id}", async (string id, UpdateTargetProfileRequest request, HttpRequest httpRequest, TargetProfileRegistry profiles, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized();
 try { var profile = await profiles.UpdateAsync(id, request, cancellationToken); return profile is null ? Results.NotFound() : Results.Ok(profile); }
 catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string,string[]> { ["profile"] = [exception.Message] }); }
});
app.MapDelete("/api/v1/profiles/{id}", async (string id, HttpRequest httpRequest, TargetProfileRegistry profiles, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized();
 try { return await profiles.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound(); }
 catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
});
app.MapGet("/api/v1/connectors", (ConnectorCatalog catalog) => Results.Ok(catalog.Types));
app.MapGet("/api/v1/connector-instances", async (HttpRequest request, ConnectorRegistry connectors, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized(); return Results.Ok((await connectors.ListAsync(cancellationToken)).Select(connector => new { connector.Id, connector.Name, connector.Type, connector.CreatedAt }));
});
app.MapPut("/api/v1/domains/{name}", async (string name, CreateDomainRequest request, HttpRequest httpRequest, DomainRegistry domains, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized();
 try { var domain = await domains.UpdateAsync(name, request, cancellationToken); return domain is null ? Results.NotFound() : Results.Ok(domain); }
 catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
});
app.MapGet("/api/v1/certificates", async (HttpRequest httpRequest, CertificateIssuanceService certificates, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await certificates.ListAsync(cancellationToken));
});
app.MapGet("/api/v1/authorities", async (HttpRequest httpRequest, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await authorities.ListAsync(cancellationToken));
});
app.MapPost("/api/v1/authorities", async (CreateAuthorityRequest request, HttpRequest httpRequest, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized();
 try { return Results.Ok(await authorities.CreateAsync(request,ct)); } catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string,string[]> { ["authority"]=[exception.Message] }); } catch (InvalidOperationException exception) { return Results.Conflict(new { detail=exception.Message }); }
});
app.MapPut("/api/v1/authorities/{id}", async (string id, UpdateAuthorityRequest request, HttpRequest httpRequest, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized();
 try { var authority=await authorities.UpdateAsync(id,request,ct); return authority is null ? Results.NotFound() : Results.Ok(authority); } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return Results.Conflict(new { detail=exception.Message }); }
});
app.MapPost("/api/v1/authorities/{id}/revoke", async (string id, HttpRequest httpRequest, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized();
 try { var authority=await authorities.RevokeAsync(id,ct); return authority is null ? Results.NotFound() : Results.Ok(authority); } catch (InvalidOperationException exception) { return Results.Conflict(new { detail=exception.Message }); }
});
app.MapDelete("/api/v1/authorities/{id}", async (string id, HttpRequest httpRequest, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized();
 try { return await authorities.DeleteAsync(id,ct) ? Results.NoContent() : Results.NotFound(); } catch (InvalidOperationException exception) { return Results.Conflict(new { detail=exception.Message }); }
});
app.MapGet("/api/v1/authorities/{id}/certificate", async (string id, string format, HttpRequest httpRequest, CertificateAuthorityService authorities, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized();
 try { var export=await authorities.ExportCertificateAsync(id,format,ct); return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName); } catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string,string[]> { ["format"]=[exception.Message] }); }
});
app.MapGet("/api/v1/renewal-plans", async (HttpRequest request, RenewalPlanRegistry plans, LocalAdministrationService administration, CancellationToken ct) => { var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,ct)?Results.Unauthorized():Results.Ok(await plans.ListAsync(ct)); });
app.MapPost("/api/v1/renewal-plans", async (CreateRenewalPlanRequest body, HttpRequest request, RenewalPlanRegistry plans, LocalAdministrationService administration, CancellationToken ct) => { var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,ct)?Results.Unauthorized():Results.Ok(await plans.AddAsync(body,ct)); });
app.MapPost("/api/v1/connector-instances", async (CreateConnectorRequest request, HttpRequest httpRequest, ConnectorRegistry connectors, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase);
 if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized();
 try { return Results.Ok(await connectors.AddAsync(request,cancellationToken)); }
 catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string,string[]> { ["connector"] = [exception.Message] }); }
 catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
});
app.MapPut("/api/v1/connector-instances/{id}", async (string id, CreateConnectorRequest request, HttpRequest httpRequest, ConnectorRegistry connectors, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase);
 if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized();
 try { var connector = await connectors.UpdateAsync(id, request, cancellationToken); return connector is null ? Results.NotFound() : Results.Ok(connector); }
 catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string,string[]> { ["connector"] = [exception.Message] }); }
 catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
});
app.MapPost("/api/v1/connector-instances/{id}/check", async (string id, HttpRequest request, ConnectorRegistry registry, ConnectorCatalog catalog, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized(); var connector=await registry.GetAsync(id,ct); var implementation=connector is null ? null : catalog.Find(connector.Type); if (connector is null || implementation is null) return Results.NotFound();
 try { return Results.Ok(await implementation.CheckAsync(new ConnectorSettings(connector.Name,connector.Type,connector.Secrets),ct)); }
 catch (Exception) { return Results.Ok(new ConnectorCheckResult(false, [], "The connector could not be reached. Check its settings and network access.")); }
});
app.MapPost("/api/v1/connector-instances/{id}/txt-test", async (string id, HttpRequest request, ConnectorRegistry registry, ConnectorCatalog catalog, LocalAdministrationService administration, CancellationToken ct) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,ct)) return Results.Unauthorized(); var connector=await registry.GetAsync(id,ct); var implementation=connector is null ? null : catalog.Find(connector.Type); if (connector is null || implementation is null) return Results.NotFound(); var name=$"_homeca-test.{request.Query["domain"]}".TrimEnd('.'); if (string.IsNullOrWhiteSpace(request.Query["domain"])) return Results.ValidationProblem(new Dictionary<string,string[]> { ["domain"]=["A domain query parameter is required."] }); var value=Guid.NewGuid().ToString("N"); await implementation.UpsertTxtRecordAsync(new ConnectorSettings(connector.Name,connector.Type,connector.Secrets),name,value,ct); await implementation.DeleteTxtRecordAsync(new ConnectorSettings(connector.Name,connector.Type,connector.Secrets),name,value,ct); return Results.NoContent();
});
app.MapGet("/api/v1/acme/directory", async (InternalAcmeService acme, CancellationToken cancellationToken) => Results.Ok(await acme.GetDirectoryAsync(cancellationToken)));
app.MapPost("/api/v1/acme/accounts", async (RegisterAcmeAccountRequest request, InternalAcmeService acme, CancellationToken cancellationToken) => Results.Ok(await acme.RegisterAccountAsync(request, cancellationToken)));
app.MapPost("/api/v1/acme/orders", async (AcmeOrderRequest request, HttpRequest httpRequest, InternalAcmeService acme, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await acme.CreateOrderAsync(request.AccountId,request.Identifiers,cancellationToken));
});
app.MapGet("/api/v1/acme/orders/{orderId}", async (string orderId, HttpRequest httpRequest, InternalAcmeService acme, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); if (!await administration.IsSessionValidAsync(token,cancellationToken)) return Results.Unauthorized(); var order=await acme.GetOrderAsync(orderId,cancellationToken); return order is null ? Results.NotFound() : Results.Ok(order);
});
app.MapPost("/api/v1/acme/orders/{orderId}/finalize", async (string orderId, FinalizeAcmeOrderRequest request, HttpRequest httpRequest, InternalAcmeService acme, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await acme.FinalizeOrderAsync(orderId,request,cancellationToken));
});
app.MapGet("/api/v1/acme/external-issuers", async (HttpRequest request, ExternalAcmeIssuerRegistry issuers, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await issuers.ListAsync(cancellationToken));
});
app.MapPost("/api/v1/acme/external-issuers", async (CreateExternalAcmeIssuerRequest request, HttpRequest httpRequest, ExternalAcmeIssuerRegistry issuers, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await issuers.AddAsync(request,cancellationToken));
});
app.MapGet("/api/v1/warnings/expiring", async (HttpRequest request, CertificateExpiryService expiry, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{ var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(expiry.GetWarnings()); });
app.MapGet("/api/v1/revocations", async (HttpRequest request, RevocationRegistry registry, LocalAdministrationService administration, CancellationToken ct) => { var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,ct)?Results.Unauthorized():Results.Ok(await registry.ListAsync(ct)); });
app.MapPost("/api/v1/revocations/{serial}/{reason}", async (string serial, string reason, HttpRequest request, RevocationRegistry registry, LocalAdministrationService administration, CancellationToken ct) => { var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,ct)?Results.Unauthorized():Results.Ok(await registry.RevokeAsync(serial,reason,ct)); });
app.MapPost("/api/v1/crl", async (HttpRequest request, CrlService crl, LocalAdministrationService administration, CancellationToken ct) => { var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,ct)?Results.Unauthorized():Results.Ok(new { path=await crl.GenerateAsync(ct) }); });
app.MapPost("/api/v1/backups", async (HttpRequest request, HomeCaStorage storage, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    if (!await administration.IsSessionValidAsync(token, cancellationToken)) return Results.Unauthorized();
    var backup = await storage.CreateBackupAsync(cancellationToken);
    return Results.Created($"/api/v1/backups/{backup.FileName}", backup);
});
app.MapPost("/api/v1/backups/{fileName}/verify", async (string fileName, HttpRequest request, HomeCaStorage storage, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=request.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await storage.VerifyBackupAsync(fileName,cancellationToken));
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
