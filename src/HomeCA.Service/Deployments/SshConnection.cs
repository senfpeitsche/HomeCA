using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Connection details for one guided SSH setup. HomeCA keeps them only for the duration of the request; they are
/// never stored, logged or returned. <see cref="ToString"/> therefore leaves out every secret.
/// </summary>
public sealed record SshCredentials(
    string Host, int Port, string UserName,
    string? Password = null, string? PrivateKey = null, string? PrivateKeyPassphrase = null, string? SudoPassword = null,
    string? HostKeyFingerprint = null, bool ReplaceHostKey = false)
{
    public override string ToString() => $"{UserName}@{Host}:{Port}";

    /// <summary>Values that must never appear in output shown to the administrator.</summary>
    internal IEnumerable<string?> Secrets => [Password, PrivateKey, PrivateKeyPassphrase, SudoPassword];
}

/// <summary>Host key of an SSH server as OpenSSH prints it: algorithm and <c>SHA256:</c> fingerprint.</summary>
public sealed record SshHostKey(string Algorithm, string Fingerprint);

public sealed record SshCommandResult(int ExitCode, string Output, string Error);

/// <summary>The server presented a host key that was not confirmed, or one that differs from the expected key.</summary>
public sealed class SshHostKeyNotTrustedException(SshHostKey presented, string? expected)
    : Exception(expected is null ? "The host key has not been confirmed yet." : "The host key differs from the confirmed host key.")
{
    public SshHostKey Presented { get; } = presented;
    public string? Expected { get; } = expected;
}

public sealed class SshSetupConnectionException(string status, string message, Exception? inner = null, string? output = null) : Exception(message, inner)
{
    public string Status { get; } = status;

    /// <summary>What the command printed before it failed, for example before a timeout.</summary>
    public string? Output { get; } = output;
}

public interface ISshSession : IDisposable
{
    SshHostKey HostKey { get; }

    /// <summary>Runs a command; <paramref name="input"/> is written to its standard input, which is then closed.</summary>
    Task<SshCommandResult> RunAsync(string command, string? input, TimeSpan timeout, CancellationToken ct);

    /// <summary>Like <see cref="RunAsync(string, string?, TimeSpan, CancellationToken)"/>, and reports every output line as it arrives.</summary>
    Task<SshCommandResult> RunAsync(string command, string? input, TimeSpan timeout, Action<string>? onOutput, CancellationToken ct) =>
        RunAsync(command, input, timeout, ct);
}

public interface ISshClientFactory
{
    /// <summary>
    /// Connects and authenticates. Throws <see cref="SshHostKeyNotTrustedException"/> before any credential is sent
    /// when the presented host key does not equal <paramref name="expectedHostKey"/> or none is expected.
    /// </summary>
    Task<ISshSession> ConnectAsync(SshCredentials credentials, string? expectedHostKey, CancellationToken ct);
}

/// <summary>SSH.NET implementation with password, keyboard-interactive and private key authentication.</summary>
public sealed class SshNetClientFactory : ISshClientFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<ISshSession> ConnectAsync(SshCredentials credentials, string? expectedHostKey, CancellationToken ct)
    {
        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? keyFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(credentials.PrivateKey))
            {
                try
                {
                    using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(credentials.PrivateKey.Replace("\r\n", "\n").Trim() + "\n"));
                    keyFile = string.IsNullOrEmpty(credentials.PrivateKeyPassphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, credentials.PrivateKeyPassphrase);
                }
                catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    throw new SshSetupConnectionException(SshSetupResult.InvalidKey, "The private key could not be read. Check the format and the passphrase.", ex);
                }
                methods.Add(new PrivateKeyAuthenticationMethod(credentials.UserName, keyFile));
            }
            if (!string.IsNullOrEmpty(credentials.Password))
            {
                methods.Add(new PasswordAuthenticationMethod(credentials.UserName, credentials.Password));
                var interactive = new KeyboardInteractiveAuthenticationMethod(credentials.UserName);
                interactive.AuthenticationPrompt += (_, e) => { foreach (var prompt in e.Prompts) prompt.Response = credentials.Password; };
                methods.Add(interactive);
            }
            if (methods.Count == 0) throw new SshSetupConnectionException(SshSetupResult.AuthenticationFailed, "A password or a private key is required.");

            var info = new Renci.SshNet.ConnectionInfo(credentials.Host, credentials.Port, credentials.UserName, methods.ToArray()) { Timeout = ConnectTimeout };
            var client = new SshClient(info);
            SshHostKey? presented = null;
            client.HostKeyReceived += (_, e) =>
            {
                presented = new SshHostKey(e.HostKeyName, "SHA256:" + e.FingerPrintSHA256);
                // Authentication only starts after this handler accepted the key, so no credential reaches an unconfirmed host.
                e.CanTrust = expectedHostKey is not null && string.Equals(presented.Fingerprint, expectedHostKey.Trim(), StringComparison.Ordinal);
            };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (presented is not null && !string.Equals(presented.Fingerprint, expectedHostKey?.Trim(), StringComparison.Ordinal))
                    throw new SshHostKeyNotTrustedException(presented, expectedHostKey);
                throw ex switch
                {
                    SshAuthenticationException => new SshSetupConnectionException(SshSetupResult.AuthenticationFailed, "The server rejected the user name or the credentials.", ex),
                    SocketException or SshConnectionException or SshOperationTimeoutException or OperationCanceledException when !ct.IsCancellationRequested
                        => new SshSetupConnectionException(SshSetupResult.Unreachable, $"No SSH connection to {credentials.Host}:{credentials.Port}: {ex.Message}", ex),
                    _ => ex
                };
            }
            return new SshNetSession(client, presented!, keyFile);
        }
        catch
        {
            keyFile?.Dispose();
            throw;
        }
    }

    private sealed class SshNetSession(SshClient client, SshHostKey hostKey, PrivateKeyFile? keyFile) : ISshSession
    {
        public SshHostKey HostKey { get; } = hostKey;

        public Task<SshCommandResult> RunAsync(string command, string? input, TimeSpan timeout, CancellationToken ct) =>
            RunAsync(command, input, timeout, null, ct);

        public async Task<SshCommandResult> RunAsync(string command, string? input, TimeSpan timeout, Action<string>? onOutput, CancellationToken ct)
        {
            using var sshCommand = client.CreateCommand(command);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            var execution = sshCommand.ExecuteAsync(limit.Token);
            // Read both streams while the command runs, so the output is known line by line and survives a timeout.
            var output = new StringBuilder();
            var error = new StringBuilder();
            var pumps = Task.WhenAll(Pump(sshCommand.OutputStream, output), Pump(sshCommand.ExtendedOutputStream, error));
            await using (var stdin = sshCommand.CreateInputStream())
            {
                if (input is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    await stdin.WriteAsync(bytes, limit.Token);
                }
            }
            try
            {
                await execution;
                await pumps.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var limitText = timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} minutes" : $"{timeout.TotalSeconds:0} seconds";
                throw new SshSetupConnectionException(SshSetupResult.Failed, $"The command did not finish within {limitText}; the output so far is below.",
                    output: Snapshot(output) + Snapshot(error));
            }
            catch (TimeoutException)
            {
                // The command ended but a stream stayed open; keep what arrived.
            }
            return new SshCommandResult(sshCommand.ExitStatus ?? -1, Snapshot(output), Snapshot(error));

            async Task Pump(Stream stream, StringBuilder target)
            {
                try
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                    while (await reader.ReadLineAsync(limit.Token) is { } line)
                    {
                        lock (target) target.Append(line).Append('\n');
                        if (!line.StartsWith("#< CLIXML", StringComparison.Ordinal) && !line.StartsWith("<Objs ", StringComparison.Ordinal)) onOutput?.Invoke(line);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }

            static string Snapshot(StringBuilder text) { lock (text) return text.ToString(); }
        }

        public void Dispose()
        {
            if (client.IsConnected) client.Disconnect();
            client.Dispose();
            keyFile?.Dispose();
        }
    }
}
