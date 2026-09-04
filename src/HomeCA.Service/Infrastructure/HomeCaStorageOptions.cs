using System.ComponentModel.DataAnnotations;

namespace HomeCA.Service.Infrastructure;

public sealed class HomeCaStorageOptions
{
    public const string SectionName = "Storage";
    public const string DefaultConfigurationPath = "data/config";

    [Required]
    public string RootPath { get; init; } = "/var/lib/homeca";

    [Required]
    public string BackupPath { get; init; } = "/var/backups/homeca";

    [Required]
    public string BackupKeyPath { get; init; } = "/etc/homeca/backup.key";

    [Required]
    public string CaKeyPath { get; init; } = "/etc/homeca/ca.key";

    /// <summary>Directory for mutable runtime configuration, such as the public URL and TLS settings.</summary>
    [Required]
    public string ConfigurationPath { get; init; } = DefaultConfigurationPath;

    /// <summary>Public base URL of the HomeCA instance, used for CRL Distribution Points in issued certificates. Example: http://homeca.int.example.org:5080</summary>
    public string? PublicUrl { get; set; }

    public static string GetConfigurationFilePath(string configurationPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("The configuration file name must not contain directory components.", nameof(fileName));

        return Path.Combine(Path.GetFullPath(configurationPath), fileName);
    }
}
