using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Deployments;

/// <summary>
/// HomeCA's own SSH key for pushing renewals. Devices accept it only with a forced command that starts their
/// agent, so the key cannot open a shell. The private key is stored encrypted with the CA key and never leaves HomeCA.
/// </summary>
public sealed class SshPushKeyService(HomeCaStorage storage, ILogger<SshPushKeyService> logger)
{
    /// <summary>Comment at the end of every authorized_keys line HomeCA writes; restore.sh removes lines by it.</summary>
    public const string KeyComment = "homeca-push";

    private readonly string _path = Path.Combine(storage.RootPath, "state", "ssh-push-key.pem");
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Public key in OpenSSH format, for example <c>ecdsa-sha2-nistp256 AAAA… homeca-push</c>.</summary>
    public async Task<string> GetPublicKeyAsync(CancellationToken ct)
    {
        using var key = await LoadOrCreateAsync(ct);
        return PublicKey(key);
    }

    /// <summary>Unencrypted PKCS#8 PEM for the SSH client; callers keep it in memory only.</summary>
    internal async Task<string> GetPrivateKeyPemAsync(CancellationToken ct)
    {
        using var key = await LoadOrCreateAsync(ct);
        return key.ExportPkcs8PrivateKeyPem();
    }

    internal static string PublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var point = new byte[1 + parameters.Q.X!.Length + parameters.Q.Y!.Length];
        point[0] = 0x04;
        parameters.Q.X.CopyTo(point, 1);
        parameters.Q.Y.CopyTo(point, 1 + parameters.Q.X.Length);
        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes("ecdsa-sha2-nistp256"));
        WriteString(blob, Encoding.ASCII.GetBytes("nistp256"));
        WriteString(blob, point);
        return $"ecdsa-sha2-nistp256 {Convert.ToBase64String(blob.ToArray())} {KeyComment}";
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private async Task<ECDsa> LoadOrCreateAsync(CancellationToken ct)
    {
        var password = storage.GetCaPfxPassword();
        await _gate.WaitAsync(ct);
        try
        {
            var key = ECDsa.Create();
            if (File.Exists(_path))
            {
                key.ImportFromEncryptedPem(await File.ReadAllTextAsync(_path, ct), password);
                return key;
            }
            key.GenerateKey(ECCurve.NamedCurves.nistP256);
            var pem = key.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, pem, ct);
            File.Move(temporaryPath, _path, false);
            logger.LogInformation("Created the SSH push key");
            return key;
        }
        finally { _gate.Release(); }
    }
}
