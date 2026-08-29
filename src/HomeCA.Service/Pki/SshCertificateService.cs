using System.Diagnostics;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Pki;

public sealed class SshCertificateService(HomeCaStorage storage)
{
    private readonly string _authorityRoot = Path.Combine(storage.RootPath, "authorities");
    private readonly string _certificateRoot = Path.Combine(storage.RootPath, "certificates", "ssh");

    public async Task<SshIssueResult> IssueAsync(SshIssueRequest request, CancellationToken cancellationToken)
    {
        if (request.Principals.Count == 0) throw new ArgumentException("At least one SSH principal is required.");
        var authority = request.Kind.Equals("host", StringComparison.OrdinalIgnoreCase) ? "ssh-host" : "ssh-user";
        var caKey = Path.Combine(_authorityRoot, authority, "ca");
        if (!File.Exists(caKey)) throw new InvalidOperationException("Initialize certificate authorities before issuing SSH certificates.");
        Directory.CreateDirectory(_certificateRoot);
        var id = Guid.NewGuid().ToString("N");
        var publicKeyPath = Path.Combine(_certificateRoot, $"{id}.pub");
        await File.WriteAllTextAsync(publicKeyPath, request.PublicKey, cancellationToken);
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-q"); start.ArgumentList.Add("-s"); start.ArgumentList.Add(caKey);
        start.ArgumentList.Add("-I"); start.ArgumentList.Add(request.Identity);
        start.ArgumentList.Add("-n"); start.ArgumentList.Add(string.Join(',', request.Principals));
        start.ArgumentList.Add("-V"); start.ArgumentList.Add($"+{request.ValidityDays}d");
        if (authority == "ssh-host") start.ArgumentList.Add("-h");
        start.ArgumentList.Add(publicKeyPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ssh-keygen.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(cancellationToken));
        var certificatePath = Path.Combine(_certificateRoot, $"{id}-cert.pub");
        return new SshIssueResult(id, authority, certificatePath, await File.ReadAllTextAsync(certificatePath, cancellationToken));
    }
}

public sealed record SshIssueRequest(string Kind, string Identity, IReadOnlyList<string> Principals, string PublicKey, int ValidityDays = 365);
public sealed record SshIssueResult(string Id, string Authority, string CertificatePath, string Certificate);
