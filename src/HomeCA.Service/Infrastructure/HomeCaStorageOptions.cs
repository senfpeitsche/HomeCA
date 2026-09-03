using System.ComponentModel.DataAnnotations;

namespace HomeCA.Service.Infrastructure;

public sealed class HomeCaStorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    public string RootPath { get; init; } = "/var/lib/homeca";

    [Required]
    public string BackupPath { get; init; } = "/var/backups/homeca";

    [Required]
    public string BackupKeyPath { get; init; } = "/etc/homeca/backup.key";

    [Required]
    public string CaKeyPath { get; init; } = "/etc/homeca/ca.key";

    /// <summary>Public base URL of the HomeCA instance, used for CRL Distribution Points in issued certificates. Example: http://homeca.int.example.org:5080</summary>
    public string? PublicUrl { get; set; }
}
