using System.Security.Cryptography.X509Certificates;
using HomeCA.Service.Deployments;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Security;

namespace HomeCA.Service.Endpoints;

static class DeploymentEndpoints
{
    public static void MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/deployment-targets").AddEndpointFilter<BearerTokenFilter>();

        api.MapGet("/", async (DeploymentTargetRegistry targets, CancellationToken ct) => Results.Ok(await targets.ListAsync(ct)));
        api.MapPost("/", async (SaveDeploymentTargetRequest request, DeploymentTargetRegistry targets, HomeCaStorage storage, CancellationToken ct) =>
        {
            if (!CertificateExists(storage, request.CertificateId)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["certificateId"] = ["The certificate does not exist."] });
            try { return Results.Ok(await targets.CreateAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPut("/{id}", async (string id, SaveDeploymentTargetRequest request, DeploymentTargetRegistry targets, HomeCaStorage storage, CancellationToken ct) =>
        {
            if (!CertificateExists(storage, request.CertificateId)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["certificateId"] = ["The certificate does not exist."] });
            try { return await targets.UpdateAsync(id, request, ct) is { } updated ? Results.Ok(updated) : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapDelete("/{id}", async (string id, DeploymentTargetRegistry targets, CancellationToken ct) =>
            await targets.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/{id}/token", async (string id, DeploymentTargetRegistry targets, CancellationToken ct) =>
            await targets.RotateTokenAsync(id, ct) is { } token ? Results.Ok(new { token }) : Results.NotFound());
        api.MapPost("/{id}/probe", async (string id, DeploymentProbeService probes, CancellationToken ct) =>
            await probes.ProbeAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        api.MapPost("/{id}/push", async (string id, DeploymentPushService pushes, CancellationToken ct) =>
            await pushes.PushAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        api.MapDelete("/{id}/push", async (string id, DeploymentTargetRegistry targets, CancellationToken ct) =>
            await targets.DisablePushAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/{id}/agent", async (string id, string? platform, HttpRequest request, DeploymentTargetRegistry targets, HomeCaStorage storage, CancellationToken ct) =>
            await targets.GetAsync(id, ct) is not { } target ? Results.NotFound()
            : platform == "windows" ? Results.Text(WindowsAgentScript.Render(PublicBaseUrl(storage, request), target), "text/plain")
            : Results.Text(DeploymentAgentScript.Render(PublicBaseUrl(storage, request), target), "text/x-shellscript"));

        // Guided setup: credentials arrive with each request and are never stored.
        var setup = endpoints.MapGroup("/api/v1/ssh-setup").AddEndpointFilter<BearerTokenFilter>();
        setup.MapPost("/detect", async (SshDetectRequest request, SshSetupService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.DetectAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connection"] = [ex.Message] }); }
        });
        setup.MapGet("/progress/{id}", (string id, int? from, SshSetupProgress progress) =>
            SshSetupProgress.IsValidId(id) ? Results.Ok(progress.Get(id, from ?? 0)) : Results.NotFound());
        setup.MapPost("/install", async (SshInstallRequest request, HttpRequest httpRequest, SshSetupService service, HomeCaStorage storage, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.InstallAsync(request, PublicBaseUrl(storage, httpRequest), ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["setup"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });

        // Agent endpoints authenticate with the target's own token, never with an administrator session.
        var agent = endpoints.MapGroup("/api/v1/deploy/{id}");
        agent.MapGet("/manifest", async (string id, HttpContext context, DeploymentTargetRegistry targets, TargetProfileRegistry profiles, HomeCaStorage storage, CancellationToken ct) =>
        {
            if (await targets.AuthenticateAsync(id, BearerToken(context), ct) is not { } target) return Results.Unauthorized();
            var exportPath = Path.Combine(storage.RootPath, "exports", target.CertificateId);
            var certificatePath = Path.Combine(storage.RootPath, "certificates", target.CertificateId, "certificate.pfx");
            if (!File.Exists(certificatePath)) return Results.NotFound();
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null);
            await targets.RecordCheckInAsync(target.Id, ct);
            var files = AvailableFiles(exportPath).ToList();
            return Results.Json(new DeploymentManifest(target.Id, target.Name, target.CertificateId, DeploymentProbeService.Fingerprint(certificate),
                certificate.NotAfter.ToUniversalTime(), files));
        });
        agent.MapGet("/files/{file}", async (string id, string file, HttpContext context, DeploymentTargetRegistry targets, DeploymentProbeService probes, HomeCaStorage storage, CancellationToken ct) =>
        {
            if (await targets.AuthenticateAsync(id, BearerToken(context), ct) is not { } target) return Results.Unauthorized();
            var exportPath = Path.Combine(storage.RootPath, "exports", target.CertificateId);
            // Only names from the fixed list reach the file system, so the route value cannot traverse directories.
            if (!AvailableFiles(exportPath).Contains(file, StringComparer.Ordinal)) return Results.NotFound();
            if (file == AgentPfxFile)
            {
                // Built on request with the issuing CA and protected with the target's own token, which the agent already holds.
                using var withKey = CertificatePfxExporter.LoadCertificateWithExportablePrivateKey(Path.Combine(storage.RootPath, "certificates", target.CertificateId, "certificate.pfx"));
                return Results.File(CertificatePfxExporter.ExportWithIssuingCertificate(withKey, Path.Combine(exportPath, "chain.pem"), BearerToken(context)!), "application/x-pkcs12", file);
            }
            if (file == "certificate.pem" && probes.CertificateFingerprint(target.CertificateId) is { } fingerprint)
                await targets.RecordDownloadAsync(target.Id, fingerprint, ct);
            return Results.File(await File.ReadAllBytesAsync(Path.Combine(exportPath, file), ct), "application/octet-stream", file);
        });
        agent.MapGet("/agent.sh", async (string id, HttpContext context, DeploymentTargetRegistry targets, HomeCaStorage storage, CancellationToken ct) =>
            await targets.AuthenticateAsync(id, BearerToken(context), ct) is { } target
                ? Results.Text(DeploymentAgentScript.Render(PublicBaseUrl(storage, context.Request), target), "text/x-shellscript")
                : Results.Unauthorized());
        agent.MapGet("/agent.ps1", async (string id, HttpContext context, DeploymentTargetRegistry targets, HomeCaStorage storage, CancellationToken ct) =>
            await targets.AuthenticateAsync(id, BearerToken(context), ct) is { } target
                ? Results.Text(WindowsAgentScript.Render(PublicBaseUrl(storage, context.Request), target), "text/plain")
                : Results.Unauthorized());
    }

    /// <summary>PFX that the agent may fetch; it exists only as long as HomeCA holds the private key.</summary>
    internal const string AgentPfxFile = "certificate.pfx";

    /// <summary>Certificate files and the profile script that exist for the certificate, in a fixed order.</summary>
    internal static IEnumerable<string> AvailableFiles(string exportPath) =>
        DeploymentAgentScript.CertificateFiles
            .Where(file => File.Exists(Path.Combine(exportPath, file)))
            .Concat(File.Exists(Path.Combine(exportPath, "key.pem")) ? [AgentPfxFile] : [])
            .Concat(new[] { ProfileScripts.FileName(ProfileScripts.Shell), ProfileScripts.FileName(ProfileScripts.PowerShell), ProfileScripts.FileName(ProfileScripts.DeviceCli) }
                .Where(file => File.Exists(Path.Combine(exportPath, file))));

    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static bool CertificateExists(HomeCaStorage storage, string? certificateId) =>
        CertificateEndpoints.IsCertificateId(certificateId) && File.Exists(Path.Combine(storage.RootPath, "certificates", certificateId!, "certificate.pfx"));

    private static string PublicBaseUrl(HomeCaStorage storage, HttpRequest request) =>
        !string.IsNullOrWhiteSpace(storage.PublicUrl) ? storage.PublicUrl : $"{request.Scheme}://{request.Host}{request.PathBase}";
}

public sealed record DeploymentManifest(string TargetId, string TargetName, string CertificateId, string Fingerprint, DateTime NotAfter, IReadOnlyList<string> Files);
