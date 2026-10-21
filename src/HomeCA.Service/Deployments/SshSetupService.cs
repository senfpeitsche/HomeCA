using System.Security.Cryptography.X509Certificates;
using System.Text;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Security;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Guided setup of a deployment target over a single SSH connection. HomeCA confirms the host key, detects the
/// device read-only and then installs the regular pull agent. Credentials exist only for the duration of a request.
/// </summary>
public sealed class SshSetupService(
    ISshClientFactory ssh,
    DeploymentTargetRegistry targets,
    TargetProfileRegistry profiles,
    CertificateIssuanceService certificates,
    CertificateAuthorityService authorities,
    DeploymentProbeService probes,
    LocalAdministrationService administration,
    SshPushKeyService pushKeys,
    DeploymentPushService pushes,
    HomeCaStorage storage,
    ILogger<SshSetupService> logger)
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(10);
    private const int MaximumOutputLength = 64 * 1024;

    public async Task<SshDetectResponse> DetectAsync(SshDetectRequest request, CancellationToken ct)
    {
        var connection = Validate(request.Connection);
        var existing = request.TargetId is null ? null : await targets.GetAsync(request.TargetId, ct);
        if (request.TargetId is not null && existing is null) return new SshDetectResponse(SshSetupResult.Failed, "The deployment target does not exist.");
        try
        {
            using var session = await ConnectAsync(connection, existing, ct);
            var facts = await DetectFactsAsync(session, ct);
            var inventory = (await certificates.ListAsync(ct, take: 500)).Select(item => (item.Id, item.Subject)).ToList();
            var suggestion = SshSetupScripts.Suggest(facts, connection.Host, await profiles.ListAsync(ct), inventory);
            await administration.RecordAuditAsync("deployment.ssh_detect", $"{connection} {session.HostKey.Fingerprint}", ct);
            return new SshDetectResponse(SshSetupResult.Ok, null, session.HostKey, Facts: facts, Suggestion: suggestion, Warnings: Warnings(facts));
        }
        catch (SshHostKeyNotTrustedException ex) { return HostKeyResponse(ex); }
        catch (SshSetupConnectionException ex) { return new SshDetectResponse(ex.Status, Clean(ex.Message, connection)); }
    }

    public async Task<SshInstallResponse> InstallAsync(SshInstallRequest request, string requestBaseUrl, CancellationToken ct)
    {
        var connection = Validate(request.Connection);
        var baseUrl = !string.IsNullOrWhiteSpace(request.HomeCaUrl) ? request.HomeCaUrl.Trim() : !string.IsNullOrWhiteSpace(storage.PublicUrl) ? storage.PublicUrl : requestBaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The HomeCA URL must be an absolute http or https URL.");
        if (baseUri.IsLoopback)
            throw new ArgumentException("The HomeCA URL points to this computer itself; enter the address the device uses to reach HomeCA.");

        DeploymentTarget? existing = null;
        if (request.TargetId is not null)
        {
            existing = await targets.GetAsync(request.TargetId, ct);
            if (existing is null) return new SshInstallResponse(SshSetupResult.Failed, "The deployment target does not exist.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("A name is required.");
            if (!CertificateExists(request.CertificateId)) throw new ArgumentException("The certificate does not exist.");
        }

        var rootCa = await authorities.GetTrustAnchorAsync("pem", ct)
            ?? throw new InvalidOperationException("No active root CA found. Initialize the PKI first.");
        var rootCaPem = Encoding.ASCII.GetString(rootCa.Content);
        using var rootCertificate = X509Certificate2.CreateFromPem(rootCaPem);
        var rootFingerprint = rootCertificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);

        string? token = null;
        try
        {
            using var session = await ConnectAsync(connection, existing, ct);
            var facts = await DetectFactsAsync(session, ct);
            if (!facts.IsWindows && !facts.HasCurl) return new SshInstallResponse(SshSetupResult.Unsupported, "curl is not installed on the device. Install curl and try again.", session.HostKey);
            var (command, prefix) = await PrivilegedShellAsync(session, connection, facts, ct);

            // Only now that the host is confirmed and HomeCA can act as root does the target or its token change.
            DeploymentTarget target;
            if (existing is null)
            {
                var created = await targets.CreateAsync(new SaveDeploymentTargetRequest(request.Name!, request.CertificateId!,
                    string.IsNullOrWhiteSpace(request.ProbeHost) ? connection.Host : request.ProbeHost, request.ProbePort ?? 443, null), ct);
                target = created.Target;
                token = created.Token;
            }
            else
            {
                target = existing;
                token = await targets.RotateTokenAsync(existing.Id, ct) ?? throw new InvalidOperationException("The deployment target no longer exists.");
            }

            // Push needs a forced command in root's authorized_keys and is only offered for POSIX devices.
            var enablePush = request.EnablePush && !facts.IsWindows;
            var script = facts.IsWindows
                ? WindowsAgentScript.RenderSetup(baseUrl, target, token, WindowsAgentScript.Render(baseUrl, target), rootCaPem, request.InstallRootCa, Random.Shared.Next(0, 60))
                : SshSetupScripts.RenderSetup(baseUrl, target, token, DeploymentAgentScript.Render(baseUrl, target), rootCaPem, rootFingerprint,
                    request.InstallRootCa, Random.Shared.Next(0, 60), enablePush ? await pushKeys.GetPublicKeyAsync(ct) : null);
            var result = await session.RunAsync(command, prefix + script, SetupTimeout, ct);
            var output = Clean(Combine(result.Output, result.Error), connection, token);

            var setupInfo = new SshSetupInfo(connection.Host, connection.Port, connection.UserName,
                session.HostKey.Algorithm, session.HostKey.Fingerprint, DateTimeOffset.UtcNow, result.ExitCode);
            await targets.RecordSshSetupAsync(target.Id, setupInfo, ct);
            DeploymentPushResult? push = null;
            if (enablePush && result.ExitCode == 0)
            {
                // A test push proves that sshd accepts the restricted key; only then does HomeCA push after renewals.
                push = await pushes.PushAsync(await targets.GetAsync(target.Id, ct) ?? target, ct);
                if (push.Status != DeploymentPushResult.Failed) await targets.RecordSshSetupAsync(target.Id, setupInfo with { PushEnabled = true, LastPush = push }, ct);
            }
            await administration.RecordAuditAsync("deployment.ssh_setup", $"{target.Name} {connection} {session.HostKey.Fingerprint} exit={result.ExitCode}", ct);
            logger.LogInformation("Guided SSH setup of deployment target {TargetId} on {Connection} finished with exit code {ExitCode}", target.Id, connection.ToString(), result.ExitCode);

            DeploymentProbeResult? probe = null;
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(target.Host)) probe = await probes.ProbeAsync(target.Id, ct);
            var status = result.ExitCode == 0 ? SshSetupResult.Ok : SshSetupResult.Failed;
            var message = result.ExitCode switch
            {
                0 => null,
                20 => "curl is not installed on the device.",
                21 => "The device cannot reach HomeCA. Check the HomeCA URL and the firewall.",
                WindowsAgentScript.NotAdministratorExitCode => "The user is not an administrator on the device.",
                _ => $"The setup script ended with exit code {result.ExitCode}."
            };
            return new SshInstallResponse(status, message, session.HostKey, await targets.GetAsync(target.Id, ct), result.ExitCode, output, probe, Push: push);
        }
        catch (SshHostKeyNotTrustedException ex)
        {
            var response = HostKeyResponse(ex);
            return new SshInstallResponse(response.Status, response.Message, response.HostKey, ExpectedHostKey: response.ExpectedHostKey);
        }
        catch (SshSetupConnectionException ex) { return new SshInstallResponse(ex.Status, Clean(ex.Message, connection, token)); }
    }

    private async Task<ISshSession> ConnectAsync(SshCredentials connection, DeploymentTarget? existing, CancellationToken ct)
    {
        // A host key confirmed earlier for the same host wins over the request; replacing it needs an explicit choice.
        var stored = existing?.Ssh is { } info && info.Host.Equals(connection.Host, StringComparison.OrdinalIgnoreCase) && info.Port == connection.Port
            ? info.HostKeyFingerprint : null;
        var expected = stored is not null && !connection.ReplaceHostKey ? stored : connection.HostKeyFingerprint;
        try
        {
            return await ssh.ConnectAsync(connection, string.IsNullOrWhiteSpace(expected) ? null : expected.Trim(), ct);
        }
        catch (SshHostKeyNotTrustedException ex) when (ex.Expected is null && stored is not null)
        {
            throw new SshHostKeyNotTrustedException(ex.Presented, stored);
        }
    }

    private static async Task<SshHostFacts> DetectFactsAsync(ISshSession session, CancellationToken ct)
    {
        var result = await session.RunAsync("sh -s", SshSetupScripts.DetectionScript, DetectionTimeout, ct);
        if (result.ExitCode == 0)
        {
            var facts = SshSetupScripts.ParseFacts(result.Output);
            // Git for Windows or MSYS2 may put an sh into the PATH of a Windows host; the agent there is still PowerShell.
            if (!IsWindowsKernel(facts.Kernel)) return facts;
        }
        var windows = await session.RunAsync(WindowsAgentScript.PowerShellCommand, WindowsAgentScript.DetectionScript, DetectionTimeout, ct);
        if (windows.ExitCode == 0 && SshSetupScripts.ParseFacts(windows.Output) is { IsWindows: true } windowsFacts) return windowsFacts;
        throw new SshSetupConnectionException(SshSetupResult.Unsupported, "The device provides neither a POSIX shell (sh) nor Windows PowerShell.");
    }

    private static bool IsWindowsKernel(string? kernel) =>
        kernel is not null && (kernel.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase) || kernel.StartsWith("MSYS", StringComparison.OrdinalIgnoreCase)
            || kernel.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase));

    /// <summary>Chooses how the setup script runs as root and checks a sudo password before the script is sent.</summary>
    private static async Task<(string Command, string Prefix)> PrivilegedShellAsync(ISshSession session, SshCredentials connection, SshHostFacts facts, CancellationToken ct)
    {
        if (facts.IsWindows)
        {
            // OpenSSH on Windows gives administrators an elevated session; there is no sudo to ask.
            if (facts.Sudo != SshHostFacts.SudoRoot) throw new SshSetupConnectionException(SshSetupResult.SudoFailed, "The user is not an administrator on the Windows device. Sign in with an administrator account.");
            return (WindowsAgentScript.PowerShellCommand, "");
        }
        switch (facts.Sudo)
        {
            case SshHostFacts.SudoRoot: return ("sh -s", "");
            case SshHostFacts.SudoWithoutPassword: return ("sudo -n sh -s", "");
            case SshHostFacts.SudoWithPassword:
                var password = string.IsNullOrEmpty(connection.SudoPassword) ? connection.Password : connection.SudoPassword;
                if (string.IsNullOrEmpty(password) || password.Contains('\n'))
                    throw new SshSetupConnectionException(SshSetupResult.SudoFailed, "sudo asks for a password. Enter the sudo password.");
                // -k ignores cached credentials, so sudo always reads the password line and never passes it to the shell.
                var check = await session.RunAsync("sudo -k -S -p '' true", password + "\n", DetectionTimeout, ct);
                if (check.ExitCode != 0) throw new SshSetupConnectionException(SshSetupResult.SudoFailed, "sudo rejected the password or the user may not use sudo.");
                return ("sudo -k -S -p '' sh -s", password + "\n");
            default:
                throw new SshSetupConnectionException(SshSetupResult.SudoFailed, "The user is not root and sudo is not available. Sign in as root or install sudo.");
        }
    }

    private static IReadOnlyList<string> Warnings(SshHostFacts facts)
    {
        var warnings = new List<string>();
        if (!facts.IsWindows && !facts.HasCurl) warnings.Add("curl-missing");
        if (facts.Sudo == SshHostFacts.SudoNone) warnings.Add("no-root");
        if (facts.Scheduler == SshHostFacts.SchedulerNone) warnings.Add("no-scheduler");
        return warnings;
    }

    private static SshDetectResponse HostKeyResponse(SshHostKeyNotTrustedException ex) => ex.Expected is null
        ? new SshDetectResponse(SshSetupResult.HostKeyUnknown, "Confirm the host key of the device.", ex.Presented)
        : new SshDetectResponse(SshSetupResult.HostKeyChanged, "The host key differs from the confirmed host key. Someone may be intercepting the connection.", ex.Presented, ex.Expected);

    private static SshCredentials Validate(SshCredentials? connection)
    {
        if (connection is null) throw new ArgumentException("Connection details are required.");
        var host = connection.Host?.Trim() ?? "";
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ArgumentException("The host must be a DNS name or an IP address.");
        if (connection.Port is < 1 or > 65535) throw new ArgumentException("The SSH port must be between 1 and 65535.");
        var user = connection.UserName?.Trim() ?? "";
        if (user.Length is 0 or > 64 || user.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))) throw new ArgumentException("A valid user name is required.");
        if (string.IsNullOrEmpty(connection.Password) && string.IsNullOrWhiteSpace(connection.PrivateKey)) throw new ArgumentException("A password or a private key is required.");
        return connection with { Host = host, UserName = user };
    }

    private bool CertificateExists(string? certificateId) =>
        Endpoints.CertificateEndpoints.IsCertificateId(certificateId) && File.Exists(Path.Combine(storage.RootPath, "certificates", certificateId!, "certificate.pfx"));

    private static string Combine(string output, string error)
    {
        error = WithoutPowerShellProgress(error);
        return string.IsNullOrWhiteSpace(error) ? output : string.IsNullOrWhiteSpace(output) ? error : output.TrimEnd() + "\n" + error;
    }

    /// <summary>Windows PowerShell writes progress records to a redirected stderr as CLIXML; they carry no message for the administrator.</summary>
    internal static string WithoutPowerShellProgress(string error) =>
        string.Join("\n", error.Replace("\r\n", "\n").Split('\n')
            .Where(line => !line.StartsWith("#< CLIXML", StringComparison.Ordinal) && !line.StartsWith("<Objs ", StringComparison.Ordinal)));

    /// <summary>Removes the token and every credential from text shown to the administrator and limits its length.</summary>
    internal static string Clean(string text, SshCredentials connection, string? token = null)
    {
        foreach (var secret in connection.Secrets.Append(token))
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 3) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text.Length <= MaximumOutputLength ? text : text[..MaximumOutputLength] + "\n…";
    }
}

public static class SshSetupResult
{
    public const string Ok = "ok";
    public const string HostKeyUnknown = "host-key-unknown";
    public const string HostKeyChanged = "host-key-changed";
    public const string AuthenticationFailed = "authentication-failed";
    public const string InvalidKey = "invalid-key";
    public const string Unreachable = "unreachable";
    public const string SudoFailed = "sudo-failed";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
}

public sealed record SshDetectRequest(SshCredentials Connection, string? TargetId = null);

public sealed record SshInstallRequest(
    SshCredentials Connection, string? TargetId = null, string? Name = null, string? CertificateId = null,
    string? ProbeHost = null, int? ProbePort = null, bool InstallRootCa = false, string? HomeCaUrl = null, bool EnablePush = false);

public sealed record SshDetectResponse(
    string Status, string? Message, SshHostKey? HostKey = null, string? ExpectedHostKey = null,
    SshHostFacts? Facts = null, SshSetupSuggestion? Suggestion = null, IReadOnlyList<string>? Warnings = null);

public sealed record SshInstallResponse(
    string Status, string? Message, SshHostKey? HostKey = null, DeploymentTarget? Target = null, int? ExitCode = null,
    string? Output = null, DeploymentProbeResult? Probe = null, string? ExpectedHostKey = null, DeploymentPushResult? Push = null);
