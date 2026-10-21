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
            PowerShell => $"{header}$ErrorActionPreference = 'Stop'\nSet-Location -LiteralPath $PSScriptRoot\n{template}\n",
            DeviceCli => $"# Profile: {profile.Id} v{profile.Version}\n# Device CLI commands: review them and enter them on the device; this is not a shell script.\n{template}\n",
            _ => $"#!/bin/sh\n{header}set -eu\ncd \"$(dirname \"$0\")\"\n{template}\n"
        };
    }
}
