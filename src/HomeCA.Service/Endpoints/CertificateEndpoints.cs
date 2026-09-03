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
 static class CertificateEndpoints
{
    public static void MapCertificateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
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
        api.MapPost("/certificates", async (IssueCertificateRequest request, CertificateIssuanceService certificates, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            try { return Results.Ok(await certificates.IssueAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["certificate"] = [ex.Message] }); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Certificate issuance failed");
                throw;
            }
        });
        api.MapPost("/certificates/{id}/revoke", async (string id, HttpRequest httpRequest, CertificateIssuanceService certificates, CancellationToken ct) =>
        {
            var reason = httpRequest.Query["reason"].FirstOrDefault() ?? "unspecified";
            return await certificates.RevokeAsync(id, reason, ct) ? Results.NoContent() : Results.NotFound();
        });
        api.MapGet("/certificates/{id}/export/pem", async (string id, HomeCaStorage storage) =>
        {
            var path = Path.Combine(storage.RootPath, "exports", id, "certificate.pem");
            if (!File.Exists(path)) return Results.NotFound();
            var name = CertificateDownloadName(storage, id);
            return Results.File(path, "application/x-pem-file", $"{name}.pem");
        });
        api.MapGet("/certificates/{id}/export/chain", async (string id, HomeCaStorage storage) =>
        {
            var path = Path.Combine(storage.RootPath, "exports", id, "chain.pem");
            if (!File.Exists(path)) return Results.NotFound();
            var name = CertificateDownloadName(storage, id);
            return Results.File(path, "application/x-pem-file", $"{name}-chain.pem");
        });
        api.MapGet("/certificates/{id}/export/key", async (string id, HomeCaStorage storage) =>
        {
            var path = Path.Combine(storage.RootPath, "exports", id, "key.pem");
            if (!File.Exists(path)) return Results.NotFound();
            var name = CertificateDownloadName(storage, id);
            return Results.File(path, "application/x-pem-file", $"{name}-key.pem");
        });
        api.MapGet("/certificates/{id}/export/fullchain", async (string id, HomeCaStorage storage) =>
        {
            var path = Path.Combine(storage.RootPath, "exports", id, "fullchain.pem");
            if (!File.Exists(path)) return Results.NotFound();
            var name = CertificateDownloadName(storage, id);
            return Results.File(path, "application/x-pem-file", $"{name}-fullchain.pem");
        });
        api.MapGet("/certificates/{id}/export/bundle", async (string id, HomeCaStorage storage) =>
        {
            var path = Path.Combine(storage.RootPath, "exports", id, "bundle.pem");
            if (!File.Exists(path)) return Results.NotFound();
            var name = CertificateDownloadName(storage, id);
            return Results.File(path, "application/x-pem-file", $"{name}-bundle.pem");
        });
        api.MapGet("/certificates/{id}/export/package", async (string id, HomeCaStorage storage, DeploymentPackageService deployments, CancellationToken ct) =>
        {
            if (!string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal)) return Results.NotFound();
            var exportPath = Path.Combine(storage.RootPath, "exports", id);
            var archive = await deployments.CreateArchiveAsync(exportPath, ct);
            if (archive is null) return Results.NotFound();
            var name = CertificateDownloadName(storage, id);
            return Results.File(archive, "application/zip", $"{name}-deployment-package.zip");
        });
        api.MapPost("/certificates/{id}/export/pfx", async (string id, PfxExportRequest request, HomeCaStorage storage) =>
        {
            var pfxPath = Path.Combine(storage.RootPath, "certificates", id, "certificate.pfx");
            if (!File.Exists(pfxPath)) return Results.NotFound();
            using var certificate = CertificatePfxExporter.LoadCertificateWithExportablePrivateKey(pfxPath);
            var chainPath = Path.Combine(storage.RootPath, "exports", id, "chain.pem");
            var bytes = CertificatePfxExporter.ExportWithIssuingCertificate(certificate, chainPath, request.Password);
            var name = CertificateDownloadName(storage, id);
            return Results.File(bytes, "application/x-pkcs12", $"{name}.pfx");
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
        api.MapPost("/ssh-certificates", async (SshIssueRequest request, SshCertificateService certificates, ILogger<global::Program> logger, CancellationToken ct) =>
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
    }

    private static string CertificateDownloadName(HomeCaStorage storage, string id)
    {
        try
        {
            var pfxPath = Path.Combine(storage.RootPath, "certificates", id, "certificate.pfx");
            if (!File.Exists(pfxPath)) return id;
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
            var cn = certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(cn)) return id;
            var sanitized = string.Concat(cn.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(sanitized) ? id : sanitized;
        }
        catch { return id; }
    }
}
