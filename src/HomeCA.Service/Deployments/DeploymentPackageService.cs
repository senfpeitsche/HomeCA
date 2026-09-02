using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HomeCA.Service.Profiles;

namespace HomeCA.Service.Deployments;

/// <summary>Creates an immutable, human-readable deployment snapshot for an issued certificate.</summary>
public sealed class DeploymentPackageService(TargetProfileRegistry profiles, ILogger<DeploymentPackageService> logger)
{
    public async Task CreateAsync(string exportPath, string certificateId, string? profileId, CancellationToken ct)
    {
        var profile = (await profiles.ListAsync(ct)).FirstOrDefault(item => item.Id == profileId)
            ?? (await profiles.ListAsync(ct)).First(item => item.Id == "generic-tls");

        try
        {
            var snapshot = new DeploymentProfileSnapshot(profile.Id, profile.Version, profile.DisplayName, profile.Documentation, profile.RenewalScriptTemplate, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(exportPath, "profile-snapshot.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), ct);
            await File.WriteAllTextAsync(Path.Combine(exportPath, "README.md"), $"# Deployment package {certificateId}\n\n## Profile\n\n{profile.DisplayName} v{profile.Version}\n\n## Installation\n\n{profile.Documentation}\n\n## Dry run\n\nReview the files and run the target-specific script with its `-WhatIf` or dry-run option where supported. No remote action is performed by HomeCA.\n\n## Rollback\n\nKeep the previous certificate and key until the service has been restarted and verified. Restore those previous files and restart the target service if verification fails.\n", ct);
            await File.WriteAllTextAsync(Path.Combine(exportPath, "install.ps1"), $"# Profile: {profile.Id} v{profile.Version}\n# Review this script before running it.\n{profile.RenewalScriptTemplate}\n", ct);
            var files = Directory.EnumerateFiles(exportPath).Select(path => new { file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
            await File.WriteAllTextAsync(Path.Combine(exportPath, "checksums.json"), JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true }), ct);
            logger.LogInformation("Created deployment package for certificate {CertificateId} with profile {ProfileId}", certificateId, profile.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to create deployment package for certificate {CertificateId}", certificateId);
            throw;
        }
    }

    /// <summary>Creates an in-memory ZIP of the immutable deployment snapshot for download.</summary>
    public async Task<byte[]?> CreateArchiveAsync(string exportPath, CancellationToken ct)
    {
        if (!Directory.Exists(exportPath)) return null;

        var files = Directory.EnumerateFiles(exportPath, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) return null;

        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var source = File.OpenRead(path);
                await source.CopyToAsync(entryStream, ct);
            }
        }

        return stream.ToArray();
    }
}

public sealed record DeploymentProfileSnapshot(string Id, string Version, string DisplayName, string Documentation, string RenewalScriptTemplate, DateTimeOffset CreatedAt);
