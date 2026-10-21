using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

using System.Text.Json.Serialization;

namespace HomeCA.Service.Infrastructure;

public sealed class HomeCaStorage
{
    private static readonly byte[] BackupMagic = "HCAB1"u8.ToArray();
    private static readonly string[] ManagedDirectories = ["authorities", "certificates", "exports", "external-certificates", "profiles", "crl", "audit", "state"];
    private readonly HomeCaStorageOptions _options;
    private readonly ILogger<HomeCaStorage> _logger;

    public HomeCaStorage(IOptions<HomeCaStorageOptions> options, ILogger<HomeCaStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
        EnsureLayout();
    }

    // Absolute, because Results.File treats a relative path as a path below wwwroot (relevant for the
    // relative "data" directory of the development configuration).
    public string RootPath => Path.GetFullPath(_options.RootPath);
    public string BackupKeyPath => _options.BackupKeyPath;
    public string CaKeyPath => _options.CaKeyPath;
    public string ConfigurationPath => Path.GetFullPath(_options.ConfigurationPath);
    public string? PublicUrl => _options.PublicUrl;
    public string? CrlDistributionBaseUrl => _options.CrlDistributionBaseUrl;

    public string GetConfigurationFilePath(string fileName) => HomeCaStorageOptions.GetConfigurationFilePath(_options.ConfigurationPath, fileName);

    public string ResolveBackupPath(string fileName) => Path.GetFullPath(Path.Combine(_options.BackupPath, fileName));

    /// <summary>Returns a stable PFX password derived from the separately stored CA key.</summary>
    public string GetCaPfxPassword()
    {
        var key = File.ReadAllBytes(_options.CaKeyPath);
        if (key.Length != 32) throw new InvalidOperationException("The CA key must contain exactly 32 bytes.");
        return Convert.ToBase64String(key);
    }

    public object Describe() => new
    {
        rootPath = _options.RootPath,
        backupPath = _options.BackupPath,
        caKeyPath = _options.CaKeyPath,
        configurationPath = ConfigurationPath,
        publicUrl = _options.PublicUrl,
        directories = ManagedDirectories,
        stateStore = Path.Combine(_options.RootPath, "state", "homeca-state.json"),
        backupFormat = "HCAB1 (AES-256-GCM encrypted ZIP)"
    };

    public async Task<BackupDescriptor> CreateBackupAsync(CancellationToken cancellationToken)
    {
        var key = await File.ReadAllBytesAsync(_options.BackupKeyPath, cancellationToken);
        if (key.Length != 32)
        {
            throw new InvalidOperationException("The backup key must contain exactly 32 bytes for AES-256-GCM.");
        }

        Directory.CreateDirectory(_options.BackupPath);
        var createdAt = DateTimeOffset.UtcNow;
        // Milliseconds keep two backups in the same second apart; CreateNew never overwrites one.
        var name = $"homeca-{createdAt:yyyyMMddHHmmssfff}.hcab";
        var target = Path.Combine(_options.BackupPath, name);

        // The archive is built in memory: a temporary plaintext ZIP on disk would expose the CA
        // keys (and survive a crash) until it was deleted.
        var plaintext = await Task.Run(() => CreateArchive(cancellationToken), cancellationToken);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var cipher = new AesGcm(key, tag.Length))
        {
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, BackupMagic);
        }
        CryptographicOperations.ZeroMemory(plaintext);

        var temporaryTarget = target + ".partial";
        await using (var output = new FileStream(temporaryTarget, FileMode.CreateNew, FileAccess.Write))
        {
            await output.WriteAsync(BackupMagic, cancellationToken);
            await output.WriteAsync(nonce, cancellationToken);
            await output.WriteAsync(tag, cancellationToken);
            await output.WriteAsync(ciphertext, cancellationToken);
        }
        // A backup that is listed must be complete.
        File.Move(temporaryTarget, target, overwrite: false);
        _logger.LogInformation("Created encrypted backup {BackupFile}", target);
        return new BackupDescriptor(name, target, createdAt);
    }

    /// <summary>
    /// Zips the data directory while the service keeps running. State files are replaced atomically
    /// via rename, so each file is read whole; in-flight ".tmp" files are skipped, and a file replaced
    /// or removed while it is read is retried or left out.
    /// </summary>
    internal byte[] CreateArchive(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(_options.RootPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var content = TryReadStable(path);
                if (content is null)
                {
                    _logger.LogWarning("Skipped {Path} in the backup because it disappeared while being read", path);
                    continue;
                }
                var entryName = Path.GetRelativePath(_options.RootPath, path).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static byte[]? TryReadStable(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) when (attempt < 2)
            {
                // Windows reports a file that is being replaced as locked; try again shortly.
                Thread.Sleep(50);
            }
        }
        return null;
    }

    public async Task<BackupVerificationResult> VerifyBackupAsync(string fileName, CancellationToken cancellationToken)
    {
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || !safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid backup file name.");
        var path = Path.Combine(_options.BackupPath, safeName);
        if (!File.Exists(path)) throw new FileNotFoundException("Backup was not found.", safeName);
        var key = await File.ReadAllBytesAsync(_options.BackupKeyPath, cancellationToken);
        var payload = await File.ReadAllBytesAsync(path, cancellationToken);
        if (key.Length != 32 || payload.Length < BackupMagic.Length + 28 || !payload.AsSpan(0, BackupMagic.Length).SequenceEqual(BackupMagic)) throw new InvalidDataException("Backup header or key is invalid.");
        var nonce = payload.AsSpan(BackupMagic.Length, 12); var tag = payload.AsSpan(BackupMagic.Length + 12, 16); var encrypted = payload.AsSpan(BackupMagic.Length + 28);
        var plaintext = new byte[encrypted.Length];
        using (var cipher = new AesGcm(key, tag.Length)) cipher.Decrypt(nonce, encrypted, tag, plaintext, BackupMagic);
        // Inspect the archive in memory; the decrypted content contains the CA keys.
        try
        {
            using var archive = new ZipArchive(new MemoryStream(plaintext, writable: false), ZipArchiveMode.Read);
            return new BackupVerificationResult(safeName, true, archive.Entries.Count);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void EnsureLayout()
    {
        Directory.CreateDirectory(_options.RootPath);
        Directory.CreateDirectory(ConfigurationPath);
        foreach (var directory in ManagedDirectories)
        {
            Directory.CreateDirectory(Path.Combine(_options.RootPath, directory));
        }

        var stateFile = Path.Combine(_options.RootPath, "state", "homeca-state.json");
        if (!File.Exists(stateFile))
        {
            var initialState = new SetupState();
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            File.WriteAllText(stateFile, JsonSerializer.Serialize(initialState, jsonOptions));
        }
    }
}

public sealed record BackupDescriptor(string FileName, string Path, DateTimeOffset CreatedAt);
public sealed record BackupVerificationResult(string FileName, bool IsValid, int EntryCount);
