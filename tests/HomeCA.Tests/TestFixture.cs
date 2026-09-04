using HomeCA.Service.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeCA.Tests;

/// <summary>Creates a temporary data directory for each test and cleans it up afterwards.</summary>
public sealed class TestFixture : IDisposable
{
    public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"homeca-test-{Guid.NewGuid():N}");
    public string BackupPath { get; }
    public string BackupKeyPath { get; }
    public string CaKeyPath { get; }
    public string ConfigurationPath { get; }

    public TestFixture()
    {
        BackupPath = Path.Combine(RootPath, "backups");
        BackupKeyPath = Path.Combine(RootPath, "backup.key");
        CaKeyPath = Path.Combine(RootPath, "ca.key");
        ConfigurationPath = Path.Combine(RootPath, "config");
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(BackupPath);
        // Create a 32-byte backup key
        File.WriteAllBytes(BackupKeyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(CaKeyPath, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    public HomeCaStorage CreateStorage()
    {
        var options = Options.Create(new HomeCaStorageOptions
        {
            RootPath = RootPath,
            BackupPath = BackupPath,
            BackupKeyPath = BackupKeyPath,
            CaKeyPath = CaKeyPath,
            ConfigurationPath = ConfigurationPath,
            PublicUrl = "http://localhost:5080"
        });
        return new HomeCaStorage(options, NullLogger<HomeCaStorage>.Instance);
    }

    public IOptions<HomeCaStorageOptions> CreateOptions() => Options.Create(new HomeCaStorageOptions
    {
        RootPath = RootPath,
        BackupPath = BackupPath,
        BackupKeyPath = BackupKeyPath,
        CaKeyPath = CaKeyPath,
        ConfigurationPath = ConfigurationPath,
        PublicUrl = "http://localhost:5080"
    });

    public void Dispose()
    {
        try { Directory.Delete(RootPath, true); } catch { /* best effort cleanup */ }
    }
}
