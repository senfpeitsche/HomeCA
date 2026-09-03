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

    public string RootPath => _options.RootPath;
    public string BackupKeyPath => _options.BackupKeyPath;
    public string CaKeyPath => _options.CaKeyPath;
    public string? PublicUrl => _options.PublicUrl;

    public string ResolveBackupPath(string fileName) => Path.Combine(_options.BackupPath, fileName);

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
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try { await File.WriteAllBytesAsync(temporaryPath, plaintext, cancellationToken); using var archive = ZipFile.OpenRead(temporaryPath); return new BackupVerificationResult(safeName, true, archive.Entries.Count); }
        finally { File.Delete(temporaryPath); }
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
