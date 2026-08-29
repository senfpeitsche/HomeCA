using HomeCA.Service.Infrastructure;
using HomeCA.Service.Security;
using HomeCA.Service.Pki;
using HomeCA.Service.Domains;
using HomeCA.Service.Profiles;
using HomeCA.Service.Connectors;
using HomeCA.Service.Acme;
using HomeCA.Service.Operations;
using HomeCA.Service.Revocation;

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
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IDnsConnector, TechnitiumDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, HetznerDnsConnector>();
builder.Services.AddSingleton<ConnectorCatalog>();
builder.Services.AddSingleton<InternalAcmeService>();
builder.Services.AddSingleton<ExternalAcmeIssuerRegistry>();
builder.Services.AddSingleton<CertificateExpiryService>();
builder.Services.AddSingleton<RevocationRegistry>();
builder.Services.AddSingleton<CrlService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

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
app.MapGet("/api/v1/profiles", async (TargetProfileRegistry profiles, CancellationToken cancellationToken) => Results.Ok(await profiles.ListAsync(cancellationToken)));
app.MapGet("/api/v1/connectors", (ConnectorCatalog catalog) => Results.Ok(catalog.Types));
app.MapPost("/api/v1/acme/orders", async (AcmeOrderRequest request, HttpRequest httpRequest, InternalAcmeService acme, LocalAdministrationService administration, CancellationToken cancellationToken) =>
{
 var token=httpRequest.Headers.Authorization.ToString().Replace("Bearer ",string.Empty,StringComparison.OrdinalIgnoreCase); return !await administration.IsSessionValidAsync(token,cancellationToken)?Results.Unauthorized():Results.Ok(await acme.CreateOrderAsync(request.AccountId,request.Identifiers,cancellationToken));
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

app.Run();
