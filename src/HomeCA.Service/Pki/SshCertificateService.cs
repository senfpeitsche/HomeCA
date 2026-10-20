using System.Diagnostics;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Pki;

public sealed class SshCertificateService(HomeCaStorage storage, ILogger<SshCertificateService> logger)
{
    private readonly string _authorityRoot = Path.Combine(storage.RootPath, "authorities");
    private readonly string _certificateRoot = Path.Combine(storage.RootPath, "certificates", "ssh");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<List<SshCertificateInventoryItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<SshCertificateInventoryItem>();
        if (!Directory.Exists(_certificateRoot)) return items;

        foreach (var certFile in Directory.EnumerateFiles(_certificateRoot, "*-cert.pub"))
        {
            var fileName = Path.GetFileNameWithoutExtension(certFile); // e.g. "abc123-cert"
            var id = fileName.Replace("-cert", "", StringComparison.Ordinal);
            try
            {
                // One ssh-keygen call per certificate; it used to be started once per field.
                var fields = await ReadCertificateFieldsAsync(certFile, cancellationToken);
                if (fields is null)
                {
                    logger.LogWarning("Could not read SSH certificate {CertificateFile} with ssh-keygen", certFile);
                    continue;
                }
                var isHost = fields.GetValueOrDefault("Type")?.Contains("host certificate", StringComparison.OrdinalIgnoreCase) == true;
                var identity = fields.GetValueOrDefault("Key ID") ?? id;
                var principals = fields.GetValueOrDefault("Principals") ?? "";
                items.Add(new SshCertificateInventoryItem(id, isHost ? "host" : "user", identity.Trim('"'), principals, new FileInfo(certFile).CreationTimeUtc, fields.GetValueOrDefault("Valid")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not parse SSH certificate {CertificateFile}", certFile);
            }
        }

        return items.OrderByDescending(i => i.IssuedAt).ToList();
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
        var fields = await ReadCertificateFieldsAsync(certPath, cancellationToken)
            ?? throw new InvalidOperationException("The SSH certificate could not be read with ssh-keygen.");
        var kind = fields.GetValueOrDefault("Type")?.Contains("host certificate", StringComparison.OrdinalIgnoreCase) == true ? "ssh-host" : "ssh-user";
        var krlPath = Path.Combine(_authorityRoot, kind, "revoked.krl");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-k");
            // Without -u, ssh-keygen replaces the KRL and silently un-revokes every earlier certificate.
            if (File.Exists(krlPath)) start.ArgumentList.Add("-u");
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(krlPath); start.ArgumentList.Add("-s"); start.ArgumentList.Add(Path.Combine(_authorityRoot, kind, "ca.pub")); start.ArgumentList.Add(certPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ssh-keygen.");
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
        }
        finally { _gate.Release(); }
        logger.LogInformation("Revoked SSH certificate {CertificateId} in {KrlPath}", id, krlPath);
        return true;
    }

    public async Task<byte[]?> GetKrlAsync(string kind, CancellationToken cancellationToken)
    {
        var authority = kind.Equals("host", StringComparison.OrdinalIgnoreCase) ? "ssh-host" : "ssh-user";
        var path = Path.Combine(_authorityRoot, authority, "revoked.krl");
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    /// <summary>Reads all fields of "ssh-keygen -L"; returns null when ssh-keygen fails.</summary>
    private static async Task<Dictionary<string, string>?> ReadCertificateFieldsAsync(string certFile, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-L"); start.ArgumentList.Add("-f"); start.ArgumentList.Add(certFile);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ssh-keygen.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            return null;
        }
        await error;
        return process.ExitCode == 0 ? ParseCertificateFields(await output) : null;
    }

    /// <summary>
    /// Parses "ssh-keygen -L" output. Multi-value fields such as Principals list their values on
    /// further-indented lines below the field name; those are joined with commas.
    /// </summary>
    internal static Dictionary<string, string> ParseCertificateFields(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fieldIndent = -1;
        string? current = null;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (indent == 0) continue; // "<file>:" header
            var trimmed = line.Trim();
            if (fieldIndent < 0) fieldIndent = indent;
            if (indent > fieldIndent && current is not null)
            {
                fields[current] = fields[current].Length == 0 ? trimmed : $"{fields[current]},{trimmed}";
                continue;
            }
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0) continue;
            current = trimmed[..colonIndex].Trim();
            fields[current] = trimmed[(colonIndex + 1)..].Trim();
        }
        return fields;
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
            // Drain stderr while waiting; a full pipe would otherwise block ssh-keygen.
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
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
