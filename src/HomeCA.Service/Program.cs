using HomeCA.Service.Notifications;
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
using HomeCA.Service.Localization;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<HomeCaStorageOptions>()
    .Bind(builder.Configuration.GetSection(HomeCaStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<HomeCaStorage>();
builder.Services.AddSingleton<HomeCaInternalHttpClient>();
builder.Services.AddSingleton<SetupStateService>();
builder.Services.AddSingleton<TlsActivationService>();
builder.Services.AddSingleton<LocalAdministrationService>();
builder.Services.AddSingleton<BrowserSessionService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CertificateAuthorityService>();
builder.Services.AddSingleton<CertificateIssuanceService>();
builder.Services.AddSingleton<SshCertificateService>();
builder.Services.AddSingleton<DomainRegistry>();
builder.Services.AddSingleton<TargetProfileRegistry>();
builder.Services.AddSingleton<DeploymentPackageService>();
builder.Services.AddSingleton<DeploymentTargetRegistry>();
builder.Services.AddSingleton<DeploymentProbeService>();
builder.Services.AddSingleton<DeploymentWebhookService>();
builder.Services.AddSingleton<ISshClientFactory, SshNetClientFactory>();
builder.Services.AddSingleton<SshSetupService>();
builder.Services.AddSingleton<SshPushKeyService>();
builder.Services.AddSingleton<DeploymentPushService>();
builder.Services.AddSingleton<CertificateRenewalService>();
builder.Services.AddSingleton<RenewalPlanRegistry>();
builder.Services.AddSingleton<RenewalNotificationSettingsRegistry>();
builder.Services.AddSingleton<RenewalMailNotificationService>();
builder.Services.AddSingleton<NotificationChannelRegistry>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IDnsConnector, TechnitiumDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, HetznerDnsConnector>();
builder.Services.AddSingleton<ConnectorCatalog>();
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<AcmeAccessPolicyRegistry>();
builder.Services.AddSingleton<AcmeIpSanPolicyRegistry>();
builder.Services.AddSingleton<ExternalAcmeIssuerRegistry>();
builder.Services.AddSingleton<ExternalAcmeService>();
builder.Services.AddSingleton<Rfc8555AcmeService>();
builder.Services.AddSingleton<CertificateExpiryService>();
builder.Services.AddSingleton<BackupScheduleService>();
builder.Services.AddSingleton<AttentionService>();
builder.Services.AddSingleton<RevocationRegistry>();
builder.Services.AddSingleton<CrlService>();
builder.Services.AddSingleton<BearerTokenFilter>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddSingleton<UpdateCheckService>();
// UiText is the resource marker in the Resources namespace, so the embedded
// resource names already include the directory. Adding a ResourcesPath here
// would make the localizer search for Resources.Resources.UiText instead.
builder.Services.AddLocalization();
builder.Services.AddHostedService<RenewalBackgroundService>();
builder.Services.AddHostedService<DeploymentProbeBackgroundService>();
builder.Services.AddHostedService<DeploymentPushBackgroundService>();
builder.Services.AddHostedService<BackupScheduleBackgroundService>();
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

var tlsChainError = ConfigureTlsCertificateChain(builder);

var app = builder.Build();
// Logging is only available once the host is built; report the TLS problem through it so it
// reaches the journal with level and category instead of bare stderr.
if (tlsChainError is not null)
    app.Logger.LogWarning(tlsChainError, "HomeCA could not configure the TLS certificate chain; Kestrel serves only the leaf certificate");

var supportedCultures = LocaleCatalog.Cultures.ToList();
var localizationOptions = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(LocaleCatalog.DefaultCulture),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures,
    FallBackToParentCultures = false,
    FallBackToParentUICultures = false
};
// Order matters: an explicit choice from the language menu wins, and only when
// no cookie is present does the browser's Accept-Language header get a say.
// Anything unsupported still falls back to English via DefaultRequestCulture.
localizationOptions.RequestCultureProviders =
[
    new CookieRequestCultureProvider { CookieName = CookieRequestCultureProvider.DefaultCookieName },
    new AcceptLanguageHeaderRequestCultureProvider()
];

// ── Load the persisted public URL, if available ─────────────────────────────
{
    var storage = app.Services.GetRequiredService<HomeCaStorage>();
    var publicUrlPath = storage.GetConfigurationFilePath("public-url.conf");
    // A systemd environment override is authoritative. In particular, TLS
    // activation installs Storage__PublicUrl with the HTTPS endpoint; an older
    // persisted HTTP URL must not overwrite it on the subsequent restart.
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Storage__PublicUrl")) && File.Exists(publicUrlPath))
    {
        var savedUrl = File.ReadAllText(publicUrlPath).Trim();
        if (!string.IsNullOrEmpty(savedUrl))
        {
            app.Services.GetRequiredService<IOptions<HomeCaStorageOptions>>().Value.PublicUrl = savedUrl;
        }
    }
}

app.UseForwardedHeaders();
// With TLS active, plain HTTP stays open only for CRLs, trust bootstrap, health and ACME;
// the UI, login and management API are redirected to HTTPS.
if (PlainHttpPolicy.TryGetHttpsPort(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"), out var httpsPort))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.IsHttps || PlainHttpPolicy.IsAllowedOverPlainHttp(context.Request.Path))
        {
            await next(context);
            return;
        }
        var target = Microsoft.AspNetCore.Http.Extensions.UriHelper.BuildAbsolute(
            "https", new HostString(context.Request.Host.Host, httpsPort), context.Request.PathBase, context.Request.Path, context.Request.QueryString);
        context.Response.Redirect(target, permanent: false, preserveMethod: true);
    });
}
app.UseRequestLocalization(localizationOptions);
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
app.MapDeploymentEndpoints();
app.MapDomainEndpoints();
app.MapConnectorEndpoints();
app.MapAcmeManagementEndpoints();
app.MapRenewalEndpoints();
app.MapOperationsEndpoints();
app.MapAuditEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

static Exception? ConfigureTlsCertificateChain(WebApplicationBuilder builder)
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (string.IsNullOrWhiteSpace(urls) || !urls.Contains("https://", StringComparison.OrdinalIgnoreCase)) return null;

    var configurationPath = builder.Configuration.GetValue<string>($"{HomeCaStorageOptions.SectionName}:ConfigurationPath")
        ?? HomeCaStorageOptions.DefaultConfigurationPath;
    var tlsConfigPath = HomeCaStorageOptions.GetConfigurationFilePath(configurationPath, "tls.json");
    if (!File.Exists(tlsConfigPath)) return null;

    try
    {
        var tlsConfig = System.Text.Json.JsonSerializer.Deserialize<TlsConfiguration>(File.ReadAllText(tlsConfigPath), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (string.IsNullOrWhiteSpace(tlsConfig?.PfxPath) || !File.Exists(tlsConfig.PfxPath)) return null;

        var chainPath = ResolveTlsChainPath(tlsConfig.PfxPath);
        if (chainPath is null) return null;

        var serverCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(tlsConfig.PfxPath, password: null);
        var exportedChain = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        exportedChain.ImportFromPemFile(chainPath);
        var issuingCertificate = exportedChain.FirstOrDefault(certificate =>
            string.Equals(certificate.Subject, serverCertificate.Issuer, StringComparison.OrdinalIgnoreCase));
        if (issuingCertificate is null) return null;

        // Kestrel's certificate path alone loads only the leaf certificate. Supply
        // the leaf and its issuer explicitly; the root CA must not be sent.
        var serverChain = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection
        {
            serverCertificate,
            issuingCertificate
        };
        // HTTPS defaults apply only to https:// endpoints; the HTTP listener kept for CRLs stays plain.
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.ServerCertificateChain = serverChain;
        }));
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

// The chain lives next to the stable TLS PFX; older installations reference the issued
// certificate directly, whose chain is in exports/{id}.
static string? ResolveTlsChainPath(string pfxPath)
{
    var certificateDirectory = Path.GetDirectoryName(pfxPath);
    if (certificateDirectory is null) return null;
    var adjacentChain = Path.Combine(certificateDirectory, "chain.pem");
    if (File.Exists(adjacentChain)) return adjacentChain;

    var storageRoot = Path.GetDirectoryName(Path.GetDirectoryName(certificateDirectory));
    if (storageRoot is null) return null;
    var exportedChain = Path.Combine(storageRoot, "exports", Path.GetFileName(certificateDirectory), "chain.pem");
    return File.Exists(exportedChain) ? exportedChain : null;
}
