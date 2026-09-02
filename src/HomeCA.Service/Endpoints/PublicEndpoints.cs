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
using System.Reflection;

namespace HomeCA.Service.Endpoints;
 static class PublicEndpoints
{
    public static void MapPublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health");
        endpoints.MapGet("/api/v1/setup/phase", (SetupStateService setupState) =>
            Results.Ok(new { phase = setupState.Current.SetupPhase.ToString().ToLowerInvariant() }));
        endpoints.MapGet("/api/v1/version", () =>
        {
            var assembly = Assembly.GetEntryAssembly()!;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var version = informational.Split('+')[0];
            var commit = informational.Contains('+') ? informational.Split('+')[1] : null;
            return Results.Ok(new { version, commit, runtime = Environment.Version.ToString() });
        });
        endpoints.MapGet("/api/v1/update-check", async (UpdateCheckService updateCheck, CancellationToken ct) =>
            Results.Ok(await updateCheck.CheckAsync(ct)));
        endpoints.MapGet("/api/v1/instance", (HomeCaStorage storage) =>
        {
            var informational = Assembly.GetEntryAssembly()!
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var version = informational.Split('+')[0];
            return Results.Ok(new { version, instance = storage.Describe() });
        });
        endpoints.MapGet("/api/v1/trust-anchor", async (CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            var info = await authorities.GetTrustAnchorInfoAsync(ct);
            return info is null ? Results.NotFound(new { detail = "No active root CA found. Initialize the PKI first." }) : Results.Ok(info);
        });
        endpoints.MapGet("/api/v1/trust-anchor/pem", async (CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            var export = await authorities.GetTrustAnchorAsync("pem", ct);
            return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
        });
        endpoints.MapGet("/api/v1/trust-anchor/der", async (CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            var export = await authorities.GetTrustAnchorAsync("der", ct);
            return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
        });
        endpoints.MapGet("/api/v1/trust-anchor/intermediate/pem", async (CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            var export = await authorities.GetTrustIntermediateAsync("pem", ct);
            return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
        });
        endpoints.MapGet("/api/v1/trust-anchor/intermediate/der", async (CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            var export = await authorities.GetTrustIntermediateAsync("der", ct);
            return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
        });
        endpoints.MapGet("/api/v1/crl/latest", async (CrlService crl, CancellationToken ct) =>
        {
            var export = await crl.GetLatestAsync(ct);
            return export is null ? Results.NotFound(new { detail = "No CRL has been generated yet. Generate one first via POST /api/v1/crl." }) : Results.File(export.Content, "application/pkix-crl", export.FileName);
        });
        endpoints.MapGet("/api/v1/crl/{authorityId}", async (string authorityId, CrlService crl, CancellationToken ct) =>
        {
            try { var export = await crl.GetAsync(authorityId, ct); return export is null ? Results.NotFound() : Results.File(export.Content, "application/pkix-crl", export.FileName); }
            catch (InvalidOperationException) { return Results.NotFound(); }
        });
        
        // ── Unauthenticated management endpoints ────────────────────────────────────
        
        endpoints.MapPost("/api/v1/setup", async (SetupRequest request, HttpContext context, LocalAdministrationService administration, CancellationToken ct) =>
        {
            if (context.Connection.RemoteIpAddress is not null && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.UserName) || request.Password.Length < 16)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = ["Username is required and password must be at least 16 characters."] });
            return await administration.SetupAsync(request, ct) ? Results.NoContent() : Results.Conflict();
        });
        endpoints.MapPost("/api/v1/login", async (LoginRequest request, HttpContext context, LocalAdministrationService administration, LoginRateLimiter rateLimiter, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (rateLimiter.IsBlocked(ip))
            {
                logger.LogWarning("Login attempt rejected because the rate limit is active for {RemoteIpAddress}", ip);
                return Results.Problem("Too many failed login attempts. Try again later.", statusCode: 429);
            }
            var loginResponse = await administration.LoginAsync(request, ct);
            if (loginResponse is null) { rateLimiter.RecordFailure(ip); return Results.Unauthorized(); }
            rateLimiter.RecordSuccess(ip);
            return Results.Ok(new { accessToken = loginResponse.AccessToken, expiresInSeconds = loginResponse.ExpiresInSeconds, mustChangePassword = loginResponse.MustChangePassword });
        });
        
        // ── Unauthenticated ACME client endpoints ───────────────────────────────────
        
        endpoints.MapGet("/api/v1/acme/directory", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.GetDirectoryAsync(ct)));
        endpoints.MapPost("/api/v1/acme/accounts", async (RegisterAcmeAccountRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.RegisterAccountAsync(request, ct)));
        endpoints.MapGet("/api/v1/connectors", (ConnectorCatalog catalog) => Results.Ok(catalog.Types));
    }

}
