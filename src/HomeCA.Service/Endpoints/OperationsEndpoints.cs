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
 static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapGet("/warnings/expiring", (CertificateExpiryService expiry) => Results.Ok(expiry.GetWarnings()));
        api.MapGet("/revocations", async (RevocationRegistry registry, CancellationToken ct) => Results.Ok(await registry.ListAsync(ct)));
        api.MapPost("/revocations/{serial}/{reason}", async (string serial, string reason, RevocationRegistry registry, CancellationToken ct) => Results.Ok(await registry.RevokeAsync(serial, reason, ct)));
        api.MapPost("/crl", async (CrlService crl, ILogger<global::Program> logger, CancellationToken ct) =>
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
        api.MapPost("/backups", async (HomeCaStorage storage, ILogger<global::Program> logger, CancellationToken ct) =>
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
        api.MapGet("/backups/{fileName}", (string fileName, HomeCaStorage storage) =>
        {
            var safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || !safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { detail = "Invalid backup file name." });
            var path = storage.ResolveBackupPath(safeName);
            return !File.Exists(path) ? Results.NotFound(new { detail = "Backup not found." }) : Results.File(path, "application/octet-stream", safeName);
        });
        api.MapPut("/backups", async (HttpRequest request, HomeCaStorage storage, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { detail = "Multipart form data required." });
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { detail = "No file uploaded." });
            var safeName = Path.GetFileName(file.FileName);
            if (!safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { detail = "Only .hcab backup files are accepted." });
            var targetPath = storage.ResolveBackupPath(safeName);
            if (File.Exists(targetPath)) return Results.Conflict(new { detail = $"Backup '{safeName}' already exists." });
            await using var stream = File.Create(targetPath);
            await file.CopyToAsync(stream, ct);
            logger.LogInformation("Uploaded backup {BackupFile}", safeName);
            return Results.Created($"/api/v1/backups/{safeName}", new { fileName = safeName });
        });
        api.MapGet("/backups", (HomeCaStorage storage) =>
        {
            var dir = storage.ResolveBackupPath(".");
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
            var files = Directory.GetFiles(dir, "*.hcab")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Select(f => new { fileName = f.Name, size = f.Length, createdAt = f.CreationTimeUtc })
                .ToArray();
            return Results.Ok(files);
        });
        api.MapDelete("/backups/{fileName}", (string fileName, HomeCaStorage storage, ILogger<global::Program> logger) =>
        {
            var safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || !safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { detail = "Invalid backup file name." });
            var path = storage.ResolveBackupPath(safeName);
            if (!File.Exists(path)) return Results.NotFound(new { detail = "Backup not found." });
            File.Delete(path);
            logger.LogInformation("Deleted backup {BackupFile}", safeName);
            return Results.NoContent();
        });
        
        // Backup key
        api.MapGet("/backup-key", async (HomeCaStorage storage, CancellationToken ct) =>
        {
            var keyPath = storage.BackupKeyPath;
            if (!File.Exists(keyPath)) return Results.NotFound(new { detail = "Backup key not found." });
            var bytes = await File.ReadAllBytesAsync(keyPath, ct);
            return Results.File(bytes, "application/octet-stream", "backup.key");
        });
        api.MapPut("/backup-key", async (HttpRequest request, HomeCaStorage storage, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { detail = "Multipart form data required." });
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { detail = "No file uploaded." });
            if (file.Length != 32) return Results.BadRequest(new { detail = "The backup key must be exactly 32 bytes (AES-256)." });
            var keyPath = storage.BackupKeyPath;
            await using var stream = File.Create(keyPath);
            await file.CopyToAsync(stream, ct);
            logger.LogInformation("Backup key uploaded to {Path}", keyPath);
            return Results.NoContent();
        });
        
        // Audit
    }

}
