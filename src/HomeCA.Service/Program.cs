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
using HomeCA.Service.Endpoints;
using System.Reflection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
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
builder.Services.AddSingleton<RenewalNotificationSettingsRegistry>();
builder.Services.AddSingleton<RenewalMailNotificationService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IDnsConnector, TechnitiumDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, HetznerDnsConnector>();
builder.Services.AddSingleton<ConnectorCatalog>();
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<AcmeAccessPolicyRegistry>();
builder.Services.AddSingleton<ExternalAcmeIssuerRegistry>();
builder.Services.AddSingleton<ExternalAcmeService>();
builder.Services.AddSingleton<Rfc8555AcmeService>();
builder.Services.AddSingleton<CertificateExpiryService>();
builder.Services.AddSingleton<RevocationRegistry>();
builder.Services.AddSingleton<CrlService>();
builder.Services.AddSingleton<BearerTokenFilter>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddSingleton<UpdateCheckService>();
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
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Only a reverse proxy running on the HomeCA host may establish a client IP.
    // Do not trust forwarding headers received directly from the network.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

ConfigureTlsCertificateChain(builder);

var app = builder.Build();

// ── Load persisted PublicUrl from /etc/homeca/public-url.conf if available ──
{
    const string publicUrlPath = "/etc/homeca/public-url.conf";
    if (File.Exists(publicUrlPath))
    {
        var savedUrl = File.ReadAllText(publicUrlPath).Trim();
        if (!string.IsNullOrEmpty(savedUrl))
        {
            var opts = app.Services.GetRequiredService<IOptions<HomeCaStorageOptions>>();
            opts.Value.PublicUrl = savedUrl;
        }
    }
}

app.UseForwardedHeaders();
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

// ── HTTP endpoints ─────────────────────────────────────────────────────────

app.MapPublicEndpoints();
app.MapRfc8555AcmeEndpoints();
app.MapSetupEndpoints();
app.MapAuthorityEndpoints();
app.MapCertificateEndpoints();
app.MapDomainEndpoints();
app.MapConnectorEndpoints();
app.MapAcmeManagementEndpoints();
app.MapRenewalEndpoints();
app.MapOperationsEndpoints();
app.MapAuditEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

static void ConfigureTlsCertificateChain(WebApplicationBuilder builder)
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (string.IsNullOrWhiteSpace(urls) || !urls.Contains("https://", StringComparison.OrdinalIgnoreCase)) return;

    const string tlsConfigPath = "/etc/homeca/tls.json";
    if (!File.Exists(tlsConfigPath)) return;

    try
    {
        var tlsConfig = System.Text.Json.JsonSerializer.Deserialize<TlsConfigDto>(File.ReadAllText(tlsConfigPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (string.IsNullOrWhiteSpace(tlsConfig?.PfxPath) || !File.Exists(tlsConfig.PfxPath)) return;

        var certificateDirectory = Path.GetDirectoryName(tlsConfig.PfxPath);
        var certificatesDirectory = certificateDirectory is null ? null : Path.GetDirectoryName(certificateDirectory);
        var storageRoot = certificatesDirectory is null ? null : Path.GetDirectoryName(certificatesDirectory);
        if (certificateDirectory is null || storageRoot is null) return;

        var certificateId = Path.GetFileName(certificateDirectory);
        var chainPath = Path.Combine(storageRoot, "exports", certificateId, "chain.pem");
        if (!File.Exists(chainPath)) return;

        var serverCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(tlsConfig.PfxPath, password: null);
        var exportedChain = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        exportedChain.ImportFromPemFile(chainPath);
        var issuingCertificate = exportedChain.FirstOrDefault(certificate =>
            string.Equals(certificate.Subject, serverCertificate.Issuer, StringComparison.OrdinalIgnoreCase));
        if (issuingCertificate is null) return;

        // Kestrel's certificate path alone loads only the leaf certificate. Supply
        // the leaf and its issuer explicitly; the root CA must not be sent.
        var serverChain = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection
        {
            serverCertificate,
            issuingCertificate
        };
        builder.WebHost.ConfigureKestrel(options => options.ConfigureEndpointDefaults(endpoint => endpoint.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.ServerCertificateChain = serverChain;
        })));
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"HomeCA could not configure the TLS certificate chain: {exception.Message}");
    }
}

record TlsConfigDto(string? HttpsUrl, string? PfxPath, string? PublicUrl, string? Hostname);
