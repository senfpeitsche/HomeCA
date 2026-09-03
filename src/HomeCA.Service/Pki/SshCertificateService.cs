using System.Diagnostics;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Pki;

public sealed class SshCertificateService(HomeCaStorage storage, ILogger<SshCertificateService> logger)
{
    private readonly string _authorityRoot = Path.Combine(storage.RootPath, "authorities");
    private readonly string _certificateRoot = Path.Combine(storage.RootPath, "certificates", "ssh");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<List<SshCertificateInventoryItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<SshCertificateInventoryItem>();
        if (!Directory.Exists(_certificateRoot)) return Task.FromResult(items);

        foreach (var certFile in Directory.EnumerateFiles(_certificateRoot, "*-cert.pub"))
        {
            var fileName = Path.GetFileNameWithoutExtension(certFile); // e.g. "abc123-cert"
            var id = fileName.Replace("-cert", "", StringComparison.Ordinal);
            try
            {
                var info = new FileInfo(certFile);
                var firstLine = File.ReadLines(certFile).FirstOrDefault() ?? "";
                var keyType = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown";
                var isHost = keyType.Contains("ssh-", StringComparison.OrdinalIgnoreCase) && ParseCertificateField(certFile, "Type")?.Contains("host", StringComparison.OrdinalIgnoreCase) == true;
                var identity = ParseCertificateField(certFile, "Key ID") ?? id;
                var principals = ParseCertificateField(certFile, "Principals") ?? "";
                var validUntil = ParseCertificateField(certFile, "Valid");
                items.Add(new SshCertificateInventoryItem(id, isHost ? "host" : "user", identity.Trim('"'), principals, info.CreationTimeUtc, validUntil));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not parse SSH certificate {CertificateFile}", certFile);
            }
        }

        return Task.FromResult(items.OrderByDescending(i => i.IssuedAt).ToList());
    }

    public Task<SshCaPublicKey?> GetCaPublicKeyAsync(string kind, CancellationToken cancellationToken)
    {
        var authority = kind.Equals("host", StringComparison.OrdinalIgnoreCase) ? "ssh-host" : "ssh-user";
        var pubKeyPath = Path.Combine(_authorityRoot, authority, "ca.pub");
        if (!File.Exists(pubKeyPath)) return Task.FromResult<SshCaPublicKey?>(null);
        var content = File.ReadAllText(pubKeyPath).Trim();
        return Task.FromResult<SshCaPublicKey?>(new SshCaPublicKey(authority, content));
    }

    public Task<string?> GetCertificateContentAsync(string id, CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(_certificateRoot, $"{id}-cert.pub");
        if (!File.Exists(certPath)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(File.ReadAllText(certPath));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var certPath = Path.Combine(_certificateRoot, $"{id}-cert.pub");
        if (!File.Exists(certPath)) return false;
        var kind = ParseCertificateField(certPath, "Type")?.Contains("host", StringComparison.OrdinalIgnoreCase) == true ? "ssh-host" : "ssh-user";
        var krlPath = Path.Combine(_authorityRoot, kind, "revoked.krl");
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-k"); start.ArgumentList.Add("-f"); start.ArgumentList.Add(krlPath); start.ArgumentList.Add("-s"); start.ArgumentList.Add(Path.Combine(_authorityRoot, kind, "ca.pub")); start.ArgumentList.Add(certPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ssh-keygen.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(cancellationToken));
        logger.LogInformation("Revoked SSH certificate {CertificateId} in {KrlPath}", id, krlPath);
        return true;
    }

    public async Task<byte[]?> GetKrlAsync(string kind, CancellationToken cancellationToken)
    {
        var authority = kind.Equals("host", StringComparison.OrdinalIgnoreCase) ? "ssh-host" : "ssh-user";
        var path = Path.Combine(_authorityRoot, authority, "revoked.krl");
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    private static string? ParseCertificateField(string certFile, string fieldName)
    {
        try
        {
            var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-L"); start.ArgumentList.Add("-f"); start.ArgumentList.Add(certFile);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex >= 0) return trimmed[(colonIndex + 1)..].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<SshIssueResult> IssueAsync(SshIssueRequest request, CancellationToken cancellationToken)
    {
        if (request.Principals.Count == 0) throw new ArgumentException("At least one SSH principal is required.");
        var authority = request.Kind.Equals("host", StringComparison.OrdinalIgnoreCase) ? "ssh-host" : "ssh-user";
        var caKey = Path.Combine(_authorityRoot, authority, "ca");
        if (!File.Exists(caKey)) throw new InvalidOperationException("Initialize certificate authorities before issuing SSH certificates.");

        logger.LogInformation("Issuing SSH {Kind} certificate for {Identity} with principals [{Principals}]",
            authority, request.Identity, string.Join(", ", request.Principals));

        Directory.CreateDirectory(_certificateRoot);
        var id = Guid.NewGuid().ToString("N");
        var publicKeyPath = Path.Combine(_certificateRoot, $"{id}.pub");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(publicKeyPath, request.PublicKey, cancellationToken);
            var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-q"); start.ArgumentList.Add("-s"); start.ArgumentList.Add(caKey);
            start.ArgumentList.Add("-z"); start.ArgumentList.Add((await NextSerialAsync(authority, cancellationToken)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-I"); start.ArgumentList.Add(request.Identity);
            start.ArgumentList.Add("-n"); start.ArgumentList.Add(string.Join(',', request.Principals));
            start.ArgumentList.Add("-V"); start.ArgumentList.Add($"+{request.ValidityDays}d");
            if (authority == "ssh-host") start.ArgumentList.Add("-h");
            start.ArgumentList.Add(publicKeyPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ssh-keygen.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                logger.LogError("ssh-keygen failed with exit code {ExitCode}: {StdErr}", process.ExitCode, stderr);
                throw new InvalidOperationException(stderr);
            }

            var certificatePath = Path.Combine(_certificateRoot, $"{id}-cert.pub");
            var certificateContent = await File.ReadAllTextAsync(certificatePath, cancellationToken);
            logger.LogInformation("Issued SSH {Kind} certificate {CertificateId} for {Identity}", authority, id, request.Identity);
            return new SshIssueResult(id, authority, certificatePath, certificateContent);
        }
        catch (Exception ex) when (ex is not ArgumentException and not InvalidOperationException and not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to issue SSH certificate for {Identity}", request.Identity);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<long> NextSerialAsync(string authority, CancellationToken ct)
    {
        var path = Path.Combine(_authorityRoot, authority, "next-serial.txt");
        var current = File.Exists(path) && long.TryParse(await File.ReadAllTextAsync(path, ct), out var value) ? value : 1;
        await File.WriteAllTextAsync(path, (current + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        return current;
    }
}

public sealed record SshIssueRequest(string Kind, string Identity, IReadOnlyList<string> Principals, string PublicKey, int ValidityDays = 365);
public sealed record SshIssueResult(string Id, string Authority, string CertificatePath, string Certificate);
public sealed record SshCertificateInventoryItem(string Id, string Kind, string Identity, string Principals, DateTime IssuedAt, string? ValidUntil);
public sealed record SshCaPublicKey(string Authority, string PublicKey);
