using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Infrastructure;

public sealed class HomeCaStorage
{
    private static readonly byte[] BackupMagic = "HCAB1"u8.ToArray();
    private static readonly string[] ManagedDirectories = ["authorities", "certificates", "exports", "profiles", "crl", "audit", "state"];
    private readonly HomeCaStorageOptions _options;
    private readonly ILogger<HomeCaStorage> _logger;

    public HomeCaStorage(IOptions<HomeCaStorageOptions> options, ILogger<HomeCaStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
        EnsureLayout();
    }

    public string RootPath => _options.RootPath;

    public object Describe() => new
    {
        rootPath = _options.RootPath,
        backupPath = _options.BackupPath,
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
        var name = $"homeca-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.hcab";
        var target = Path.Combine(_options.BackupPath, name);
        var archivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");

        try
        {
            await Task.Run(() => ZipFile.CreateFromDirectory(_options.RootPath, archivePath, CompressionLevel.SmallestSize, false), cancellationToken);
            var plaintext = await File.ReadAllBytesAsync(archivePath, cancellationToken);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var cipher = new AesGcm(key, tag.Length))
            {
                cipher.Encrypt(nonce, plaintext, ciphertext, tag, BackupMagic);
            }

            await using var output = File.Create(target);
            await output.WriteAsync(BackupMagic, cancellationToken);
            await output.WriteAsync(nonce, cancellationToken);
            await output.WriteAsync(tag, cancellationToken);
            await output.WriteAsync(ciphertext, cancellationToken);
            _logger.LogInformation("Created encrypted backup {BackupFile}", target);
            return new BackupDescriptor(name, target, DateTimeOffset.UtcNow);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private void EnsureLayout()
    {
        Directory.CreateDirectory(_options.RootPath);
        foreach (var directory in ManagedDirectories)
        {
            Directory.CreateDirectory(Path.Combine(_options.RootPath, directory));
        }

        var stateFile = Path.Combine(_options.RootPath, "state", "homeca-state.json");
        if (!File.Exists(stateFile))
        {
            File.WriteAllText(stateFile, JsonSerializer.Serialize(new { version = 1, createdAt = DateTimeOffset.UtcNow }));
        }
    }
}

public sealed record BackupDescriptor(string FileName, string Path, DateTimeOffset CreatedAt);
