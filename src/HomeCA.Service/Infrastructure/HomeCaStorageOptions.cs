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
}
