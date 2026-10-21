using System.Text.RegularExpressions;

namespace HomeCA.Service.Profiles;

/// <summary>Decides how a profile's script is delivered: as a POSIX shell script, a PowerShell script or device CLI commands.</summary>
public static class ProfileScripts
{
    public const string Shell = "sh";
    public const string PowerShell = "ps1";
    public const string DeviceCli = "cli";

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { Shell, PowerShell, DeviceCli };

    // Verb-Noun cmdlets (Import-PfxCertificate) or PowerShell variables such as $env:TEMP.
    private static readonly Regex PowerShellPattern = new(@"\b(Get|Set|New|Remove|Import|Export|Copy|Move|Restart|Start|Stop|Read|Write)-[A-Z][A-Za-z]+\b|\$env:", RegexOptions.Compiled);

    /// <summary>Uses the explicit script type and falls back to detecting PowerShell for profiles created before script types existed.</summary>
    public static string Resolve(TargetProfile profile) => Resolve(profile.ScriptType, profile.RenewalScriptTemplate);

    public static string Resolve(string? scriptType, string? template)
    {
        if (scriptType is not null && Types.Contains(scriptType)) return scriptType;
        return PowerShellPattern.IsMatch(template ?? "") ? PowerShell : Shell;
    }

    public static string FileName(string scriptType) => scriptType switch
    {
        PowerShell => "install.ps1",
        DeviceCli => "device-commands.txt",
        _ => "install.sh"
    };

    public static string Render(TargetProfile profile, string scriptType)
    {
        var template = profile.RenewalScriptTemplate.Replace("\r\n", "\n");
        var header = $"# Profile: {profile.Id} v{profile.Version}\n# Review this script before running it.\n";
        return scriptType switch
        {
            PowerShell => $"{header}$ErrorActionPreference = 'Stop'\nSet-Location -LiteralPath $PSScriptRoot\n{PowerShellBackupHelpers}{template}\n",
            DeviceCli => $"# Profile: {profile.Id} v{profile.Version}\n# Device CLI commands: review them and enter them on the device; this is not a shell script.\n{template}\n",
            _ => $"#!/bin/sh\n{header}set -eu\ncd \"$(dirname \"$0\")\"\n{ShellBackupHelpers}{template}\n"
        };
    }

    /// <summary>
    /// Shell functions every rendered install.sh starts with. <c>cp</c> and <c>install</c> save each destination before
    /// they write it; templates call <c>homeca_backup</c> for files they write in other ways. Each backup directory gets
    /// files.txt and a restore.sh that copies saved files back and removes files that did not exist before.
    /// </summary>
    internal static readonly string ShellBackupHelpers = """
        # Backups: every file written through cp, install or homeca_backup is saved first; run restore.sh to undo.
        HOMECA_BACKUP_DIR="${HOMECA_BACKUP_DIR:-/var/backups/homeca/manual/$(date +%Y%m%d-%H%M%S)-install}"
        homeca_quote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }
        homeca_backup() {
            for homeca_path in "$@"; do
                case "$homeca_path" in /*) ;; *) homeca_path="$(pwd)/$homeca_path" ;; esac
                if [ -d "$homeca_path" ]; then continue; fi
                if [ ! -f "$HOMECA_BACKUP_DIR/restore.sh" ]; then
                    mkdir -p "$HOMECA_BACKUP_DIR"
                    chmod 700 "$HOMECA_BACKUP_DIR"
                    printf '#!/bin/sh\n# Restores the state before the HomeCA change that created this directory.\nset -eu\ncd "$(dirname "$0")"\nif [ -f ./before-restore.sh ]; then sh ./before-restore.sh; fi\n' > "$HOMECA_BACKUP_DIR/restore.sh"
                    chmod 700 "$HOMECA_BACKUP_DIR/restore.sh"
                    : > "$HOMECA_BACKUP_DIR/files.txt"
                fi
                if grep -Fxq -- "$homeca_path" "$HOMECA_BACKUP_DIR/files.txt"; then continue; fi
                printf '%s\n' "$homeca_path" >> "$HOMECA_BACKUP_DIR/files.txt"
                if [ -e "$homeca_path" ]; then
                    mkdir -p "$HOMECA_BACKUP_DIR/files$(dirname "$homeca_path")"
                    command cp -p "$homeca_path" "$HOMECA_BACKUP_DIR/files$homeca_path"
                    printf 'mkdir -p %s && cp -p %s %s\n' "$(homeca_quote "$(dirname "$homeca_path")")" "$(homeca_quote "files$homeca_path")" "$(homeca_quote "$homeca_path")" >> "$HOMECA_BACKUP_DIR/restore.sh"
                else
                    printf 'rm -f -- %s\n' "$(homeca_quote "$homeca_path")" >> "$HOMECA_BACKUP_DIR/restore.sh"
                fi
            done
        }
        # Saves the files cp or install is about to write: the destination, or each source's name inside a destination directory.
        homeca_backup_destinations() {
            homeca_kind="$1"; shift
            homeca_dest=""; homeca_sources=""; homeca_directory=""; homeca_skip=""
            for homeca_arg in "$@"; do
                if [ -n "$homeca_skip" ]; then
                    if [ "$homeca_skip" = target ]; then homeca_directory="$homeca_arg"; fi
                    homeca_skip=""; continue
                fi
                case "$homeca_arg" in
                    -d|--directory) if [ "$homeca_kind" = install ]; then return 0; fi ;;
                    -t) homeca_skip=target ;;
                    -o|-g|-m|-S) if [ "$homeca_kind" = install ]; then homeca_skip=value; fi ;;
                    -*) ;;
                    *)
                        if [ -n "$homeca_dest" ]; then homeca_sources="$homeca_sources$homeca_dest
        "; fi
                        homeca_dest="$homeca_arg" ;;
                esac
            done
            if [ -n "$homeca_directory" ]; then
                homeca_sources="$homeca_sources$homeca_dest
        "
                homeca_dest="$homeca_directory/"
            fi
            if [ -z "$homeca_dest" ] || [ -z "$homeca_sources" ]; then return 0; fi
            case "$homeca_dest" in */) homeca_into=1 ;; *) homeca_into="" ;; esac
            if [ -d "$homeca_dest" ] || [ "$(printf '%s' "$homeca_sources" | grep -c .)" -gt 1 ]; then homeca_into=1; fi
            if [ -z "$homeca_into" ]; then homeca_backup "$homeca_dest"; return 0; fi
            printf '%s' "$homeca_sources" | while IFS= read -r homeca_source; do
                homeca_backup "${homeca_dest%/}/$(basename "$homeca_source")"
            done
        }
        cp() { homeca_backup_destinations cp "$@"; command cp "$@"; }
        install() { homeca_backup_destinations install "$@"; command install "$@"; }

        """.Replace("\r\n", "\n");

    /// <summary>
    /// PowerShell counterpart of <see cref="ShellBackupHelpers"/> for install.ps1 and the Windows setup.
    /// <c>Backup-HomeCAFile</c> saves files before they change; <c>Add-HomeCARestoreStep</c> records how to undo a
    /// change that is not a file, such as a certificate store entry or an IIS binding. The backup directory is
    /// readable by SYSTEM and Administrators only, because it may hold earlier keys and tokens.
    /// </summary>
    internal static readonly string PowerShellBackupHelpers = """
        # Backups: every change goes through Backup-HomeCAFile or Add-HomeCARestoreStep first; run restore.ps1 to undo.
        if (-not $env:HOMECA_BACKUP_DIR) { $env:HOMECA_BACKUP_DIR = Join-Path $env:ProgramData ('HomeCA\Backups\manual\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-install') }
        function ConvertTo-HomeCALiteral([string] $Value) { "'" + $Value.Replace("'", "''") + "'" }
        function Initialize-HomeCABackup {
            $directory = $env:HOMECA_BACKUP_DIR
            if (-not (Test-Path -LiteralPath (Join-Path $directory 'restore.ps1'))) {
                New-Item -ItemType Directory -Force -Path (Join-Path $directory 'files') | Out-Null
                # SYSTEM, Administrators and the account running this script (SYSTEM for the agent) keep access.
                & icacls.exe $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ('*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value + ':(OI)(CI)F') | Out-Null
                Set-Content -LiteralPath (Join-Path $directory 'restore.ps1') -Encoding UTF8 -Value @(
                    '# Restores the state before the HomeCA change that created this directory. Run it in an elevated PowerShell.',
                    '$ErrorActionPreference = ''Stop''',
                    'Set-Location -LiteralPath $PSScriptRoot')
                New-Item -ItemType File -Force -Path (Join-Path $directory 'files.txt') | Out-Null
            }
            $directory
        }
        function Add-HomeCARestoreStep([string] $Command) {
            $directory = Initialize-HomeCABackup
            Add-Content -LiteralPath (Join-Path $directory 'restore.ps1') -Encoding UTF8 -Value $Command
        }
        function Backup-HomeCAFile([string[]] $Path) {
            $directory = Initialize-HomeCABackup
            $list = Join-Path $directory 'files.txt'
            foreach ($item in $Path) {
                $full = [IO.Path]::GetFullPath($item)
                if (@(Get-Content -LiteralPath $list) -contains $full) { continue }
                Add-Content -LiteralPath $list -Value $full
                if (Test-Path -LiteralPath $full -PathType Leaf) {
                    $relative = 'files\' + $full.Replace(':', '').TrimStart('\')
                    $copy = Join-Path $directory $relative
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $copy) | Out-Null
                    Copy-Item -LiteralPath $full -Destination $copy -Force
                    Add-HomeCARestoreStep ('New-Item -ItemType Directory -Force -Path ' + (ConvertTo-HomeCALiteral (Split-Path -Parent $full)) + ' | Out-Null; Copy-Item -LiteralPath ' + (ConvertTo-HomeCALiteral $relative) + ' -Destination ' + (ConvertTo-HomeCALiteral $full) + ' -Force')
                } else {
                    Add-HomeCARestoreStep ('Remove-Item -LiteralPath ' + (ConvertTo-HomeCALiteral $full) + ' -Force -ErrorAction SilentlyContinue')
                }
            }
        }
        # Adds a certificate with its key to LocalMachine\<Store>; restore.ps1 removes it again if it was new.
        function Import-HomeCACertificate([string] $PfxPath, [string] $Store = 'My') {
            $password = if ($env:HOMECA_PFX_PASSWORD) { $env:HOMECA_PFX_PASSWORD } else { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host -AsSecureString 'PFX password'))) }
            $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]'MachineKeySet,PersistKeySet'
            $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 ((Resolve-Path -LiteralPath $PfxPath).Path, $password, $flags)
            if (-not (Test-Path -LiteralPath ("Cert:\LocalMachine\$Store\" + $certificate.Thumbprint))) {
                Add-HomeCARestoreStep ('Remove-Item -LiteralPath ' + (ConvertTo-HomeCALiteral ("Cert:\LocalMachine\$Store\" + $certificate.Thumbprint)) + ' -DeleteKey -ErrorAction SilentlyContinue')
                $target = New-Object Security.Cryptography.X509Certificates.X509Store ($Store, 'LocalMachine')
                $target.Open('ReadWrite')
                try { $target.Add($certificate) } finally { $target.Close() }
            }
            # Written to the console directly, so the function returns only the certificate.
            [Console]::Out.WriteLine("homeca-install: certificate $($certificate.Thumbprint) is in LocalMachine\$Store")
            $certificate
        }

        """.Replace("\r\n", "\n");
}
