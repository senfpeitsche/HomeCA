using System.Text;
using HomeCA.Service.Profiles;

namespace HomeCA.Service.Deployments;

/// <summary>
/// PowerShell counterpart of <see cref="DeploymentAgentScript"/> and <see cref="SshSetupScripts"/> for Windows targets.
/// The scripts run in Windows PowerShell 5.1 and PowerShell 7 and stay ASCII, because a console code page would
/// otherwise mangle other characters.
/// </summary>
public static class WindowsAgentScript
{
    /// <summary>Exit code of the setup when the signed-in user is not an administrator.</summary>
    public const int NotAdministratorExitCode = 30;

    public static string AgentPath(string targetId) => $@"C:\ProgramData\HomeCA\Agent\agent-{DeploymentAgentScript.ShortId(targetId)}.ps1";

    public static string BackupRoot(string targetId) => $@"C:\ProgramData\HomeCA\Backups\{DeploymentAgentScript.ShortId(targetId)}";

    public static string TaskName(string targetId) => $"Agent {DeploymentAgentScript.ShortId(targetId)}";

    public const string TaskPath = @"\HomeCA\";

    /// <summary>
    /// Command for an SSH session on Windows, whether the default shell is cmd or PowerShell. It reads the script from
    /// standard input as UTF-8, runs it from a temporary file in the user's profile and returns its exit code. The
    /// bootstrap is passed encoded, because cmd limits command lines to 8191 characters and the script is longer.
    /// </summary>
    public static readonly string PowerShellCommand = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
        + Convert.ToBase64String(Encoding.Unicode.GetBytes("""
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            $buffer = New-Object IO.MemoryStream
            [Console]::OpenStandardInput().CopyTo($buffer)
            $file = Join-Path $env:TEMP ('homeca-' + [Guid]::NewGuid().ToString('N') + '.ps1')
            [IO.File]::WriteAllText($file, [Text.Encoding]::UTF8.GetString($buffer.ToArray()), (New-Object Text.UTF8Encoding $true))
            $code = 1
            try { & $file; $code = $LASTEXITCODE } catch { [Console]::Error.WriteLine($_.Exception.Message) } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
            if ($null -eq $code) { $code = 0 }
            exit $code
            """.Replace("\r\n", "\n")));

    /// <summary>Read-only detection in the <c>key=value</c> format of <see cref="SshSetupScripts.ParseFacts"/>.</summary>
    public static readonly string DetectionScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        function kv([string] $Key, [string] $Value) { if ($Value) { Write-Output ($Key + '=' + ($Value -replace "[`r`n]", '')) } }
        kv platform windows
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        kv user $identity.Name
        $administrator = ([Security.Principal.WindowsPrincipal] $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($administrator) { kv uid 0; kv sudo root } else { kv uid 1000; kv sudo none }
        kv hostname $env:COMPUTERNAME
        $system = Get-CimInstance Win32_ComputerSystem
        if ($system.PartOfDomain) { kv fqdn (($env:COMPUTERNAME + '.' + $system.Domain).ToLowerInvariant()) } else { kv fqdn (([Net.Dns]::GetHostEntry('').HostName).ToLowerInvariant()) }
        kv os ((Get-CimInstance Win32_OperatingSystem).Caption)
        kv kernel Windows
        kv arch $env:PROCESSOR_ARCHITECTURE
        foreach ($address in @(Get-NetIPAddress | Where-Object { $_.PrefixOrigin -ne 'WellKnown' -and $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -notlike 'fe80*' })) { kv address $address.IPAddress }
        kv tool powershell
        kv scheduler taskscheduler
        kv truststore windows
        if (Get-Service W3SVC) { kv service iis }
        if ((Get-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server').fDenyTSConnections -eq 0) { kv service rdp }
        exit 0

        """.Replace("\r\n", "\n");

    /// <summary>
    /// Renders agent.ps1. It fetches the manifest, compares the fingerprint, downloads the files including a PFX protected
    /// with the target token, swaps them in and runs install.ps1. A lock file prevents parallel runs (exit code 75).
    /// </summary>
    public static string Render(string? baseUrl, DeploymentTarget target) => $$"""
        # HomeCA deployment agent for "{{Ascii(target.Name)}}" ({{target.Id}}).
        # Fetches the current certificate when it changed and runs the profile script (install.ps1) if present.
        # Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File {{AgentPath(target.Id)}} [-Force]
        param([switch] $Force)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $HomeCaUrl = '{{DeploymentAgentScript.Normalize(baseUrl)}}'
        $TargetId = '{{target.Id}}'
        $Root = if ($env:HOMECA_AGENT_ROOT) { $env:HOMECA_AGENT_ROOT } else { Join-Path $env:ProgramData 'HomeCA\Agent' }
        $TokenFile = Join-Path $Root "$TargetId.token"
        $SettingsFile = Join-Path $Root "$TargetId.settings.json"
        $StateDir = Join-Path $Root $TargetId
        # install.ps1 saves everything it changes below BackupRoot; the newest KeepBackups are kept.
        $BackupRoot = if ($env:HOMECA_BACKUP_ROOT) { $env:HOMECA_BACKUP_ROOT } else { Join-Path $env:ProgramData 'HomeCA\Backups\{{DeploymentAgentScript.ShortId(target.Id)}}' }
        $KeepBackups = {{DeploymentAgentScript.KeptBackups}}
        # Set HOMECA_RUN_SCRIPT=0 to only download the files.
        $RunScript = $env:HOMECA_RUN_SCRIPT -ne '0'
        $CaFile = $null
        if (Test-Path -LiteralPath $SettingsFile) { $CaFile = (Get-Content -Raw -LiteralPath $SettingsFile | ConvertFrom-Json).caFile }
        if ($env:HOMECA_CA_FILE) { $CaFile = $env:HOMECA_CA_FILE }

        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        if ($CaFile) {
            # Windows does not trust the HomeCA root CA yet: accept a server certificate only if it chains to this copy.
            Add-Type -TypeDefinition @"
        using System.Net.Security;
        using System.Security.Cryptography.X509Certificates;
        public static class HomeCaTrust {
            public static X509Certificate2 Root;
            public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
                if (errors == SslPolicyErrors.None) return true;
                if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                var custom = new X509Chain();
                custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                custom.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                custom.ChainPolicy.ExtraStore.Add(Root);
                if (chain != null) foreach (var element in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
                if (!custom.Build(new X509Certificate2(certificate))) return false;
                return custom.ChainElements[custom.ChainElements.Count - 1].Certificate.Thumbprint == Root.Thumbprint;
            }
        }
        "@
            [HomeCaTrust]::Root = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $CaFile
            [Net.ServicePointManager]::ServerCertificateValidationCallback = [Delegate]::CreateDelegate([Net.Security.RemoteCertificateValidationCallback], [HomeCaTrust].GetMethod('Validate'))
        }

        $token = (Get-Content -Raw -LiteralPath $TokenFile).Trim()
        function Get-HomeCAFile([string] $Name, [string] $Destination) {
            $client = New-Object Net.WebClient
            $client.Headers['Authorization'] = "Bearer $token"
            try { $client.DownloadFile("$HomeCaUrl/api/v1/deploy/$TargetId/$Name", $Destination) } finally { $client.Dispose() }
        }

        New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
        # The scheduled task and a manual run may start at the same time; Windows releases the lock when the process ends.
        try { $lock = [IO.File]::Open("$StateDir.lock", 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { [Console]::Error.WriteLine('homeca-agent: another run is in progress'); exit 75 }

        $exitCode = 0
        $incoming = Join-Path $StateDir ('.incoming-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $incoming | Out-Null
        try {
            Get-HomeCAFile 'manifest' (Join-Path $incoming 'manifest.json')
            $manifest = Get-Content -Raw -LiteralPath (Join-Path $incoming 'manifest.json') | ConvertFrom-Json
            $fingerprint = [string] $manifest.fingerprint
            if (-not $fingerprint) { throw 'homeca-agent: the manifest has no fingerprint' }
            $current = Join-Path $StateDir 'current'
            $installed = Join-Path $current 'fingerprint'
            if (-not $Force -and (Test-Path -LiteralPath $installed) -and ((Get-Content -Raw -LiteralPath $installed).Trim() -eq $fingerprint)) {
                if ($env:SSH_CONNECTION) { Write-Output "homeca-agent: certificate $fingerprint is already installed" }
            }
            else {
                foreach ($file in @($manifest.files)) { Get-HomeCAFile "files/$file" (Join-Path $incoming $file) }
                $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $incoming 'certificate.pem')
                $sha256 = [Security.Cryptography.SHA256]::Create()
                $actual = [BitConverter]::ToString($sha256.ComputeHash($certificate.RawData)).Replace('-', '').ToLowerInvariant()
                if ($actual -ne $fingerprint) { throw 'homeca-agent: downloaded certificate does not match the manifest' }
                Set-Content -LiteralPath (Join-Path $incoming 'fingerprint') -Value $fingerprint

                $previous = Join-Path $StateDir 'previous'
                if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
                if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $previous }
                Move-Item -LiteralPath $incoming -Destination $current
                $incoming = $null

                if ($RunScript -and (Test-Path -LiteralPath (Join-Path $current 'install.ps1'))) {
                    $env:HOMECA_BACKUP_DIR = Join-Path $BackupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-renewal')
                    $env:HOMECA_PFX_PASSWORD = $token
                    $shell = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
                    Push-Location -LiteralPath $current
                    try { & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\install.ps1; $scriptExit = $LASTEXITCODE }
                    finally { Pop-Location; Remove-Item Env:\HOMECA_PFX_PASSWORD -ErrorAction SilentlyContinue }
                    if ($scriptExit -ne 0) { throw "homeca-agent: install.ps1 ended with exit code $scriptExit" }
                    if (Test-Path -LiteralPath (Join-Path $env:HOMECA_BACKUP_DIR 'restore.ps1')) { Write-Output "homeca-agent: backup in $($env:HOMECA_BACKUP_DIR) (undo with restore.ps1)" }
                }
                Write-Output "homeca-agent: installed certificate $fingerprint"
            }
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            $exitCode = 1
        }
        finally {
            if ($incoming -and (Test-Path -LiteralPath $incoming)) { Remove-Item -LiteralPath $incoming -Recurse -Force }
            if (Test-Path -LiteralPath $BackupRoot) {
                @(Get-ChildItem -LiteralPath $BackupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip $KeepBackups) | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
            }
            $lock.Dispose()
        }
        exit $exitCode

        """.Replace("\r\n", "\n");

    /// <summary>
    /// Commands for an elevated PowerShell on a device without OpenSSH, for example in a remoting session over WinRM.
    /// The token appears here only right after it was created.
    /// </summary>
    public static string RenderInstallCommands(string? baseUrl, DeploymentTarget target, string token)
    {
        var url = DeploymentAgentScript.Normalize(baseUrl);
        var shortId = DeploymentAgentScript.ShortId(target.Id);
        return $$"""
            $root = Join-Path $env:ProgramData 'HomeCA\Agent'; New-Item -ItemType Directory -Force -Path $root | Out-Null
            icacls.exe $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
            Set-Content -LiteralPath "$root\{{target.Id}}.token" -Value '{{token}}'
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $client = New-Object Net.WebClient; $client.Headers['Authorization'] = 'Bearer {{token}}'; $client.DownloadFile('{{url}}/api/v1/deploy/{{target.Id}}/agent.ps1', "$root\agent-{{shortId}}.ps1")
            Register-ScheduledTask -TaskPath '{{TaskPath}}' -TaskName '{{TaskName(target.Id)}}' -Force -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File $root\agent-{{shortId}}.ps1") -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes({{Random.Shared.Next(0, 60)}}) -RepetitionInterval (New-TimeSpan -Hours 1)) -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) | Out-Null
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root\agent-{{shortId}}.ps1"
            """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Renders the Windows setup. Like the Linux setup it saves everything it replaces or creates first (files, the
    /// scheduled task and the root CA entry) and writes restore.ps1, then installs token, agent and task and runs the agent.
    /// </summary>
    public static string RenderSetup(string baseUrl, DeploymentTarget target, string token, string agentScript, string rootCaPem, bool installRootCa, int minute)
    {
        if (token.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new ArgumentException("The token contains unexpected characters.");
        if (agentScript.Contains("\n'@") || rootCaPem.Contains("'@")) throw new ArgumentException("A here-string delimiter appears in the content.");
        var url = DeploymentAgentScript.Normalize(baseUrl);
        var shortId = DeploymentAgentScript.ShortId(target.Id);
        return $$"""
            # HomeCA guided setup for "{{Ascii(target.Name)}}" ({{target.Id}}).
            # Everything this script replaces or creates is saved first; restore.ps1 in the backup directory undoes it.
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                [Console]::Error.WriteLine('homeca-setup: the user must be an administrator')
                exit {{NotAdministratorExitCode}}
            }
            $env:HOMECA_BACKUP_DIR = Join-Path $env:ProgramData ('HomeCA\Backups\{{shortId}}\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-setup')
            {{ProfileScripts.PowerShellBackupHelpers}}
            $Root = Join-Path $env:ProgramData 'HomeCA\Agent'
            $TokenFile = Join-Path $Root '{{target.Id}}.token'
            $SettingsFile = Join-Path $Root '{{target.Id}}.settings.json'
            $CaCopy = Join-Path $Root 'homeca-root.pem'
            $AgentFile = Join-Path $Root 'agent-{{shortId}}.ps1'
            $ManifestUrl = '{{url}}/api/v1/deploy/{{target.Id}}/manifest'
            $TaskPath = '{{TaskPath}}'
            $TaskName = '{{TaskName(target.Id)}}'

            # Token, keys and backups are readable by SYSTEM and Administrators only.
            foreach ($directory in @($Root, (Join-Path $env:ProgramData 'HomeCA\Backups'))) {
                New-Item -ItemType Directory -Force -Path $directory | Out-Null
                & icacls.exe $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
            }
            Backup-HomeCAFile @($TokenFile, $SettingsFile, $CaCopy, $AgentFile)
            $existingTask = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
            if ($existingTask) {
                $backupDirectory = Initialize-HomeCABackup
                Export-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName | Set-Content -LiteralPath (Join-Path $backupDirectory 'task.xml') -Encoding Unicode
                Add-HomeCARestoreStep ('Register-ScheduledTask -TaskPath ' + (ConvertTo-HomeCALiteral $TaskPath) + ' -TaskName ' + (ConvertTo-HomeCALiteral $TaskName) + ' -Xml (Get-Content -Raw -LiteralPath task.xml) -Force | Out-Null')
            } else {
                Add-HomeCARestoreStep ('Unregister-ScheduledTask -TaskPath ' + (ConvertTo-HomeCALiteral $TaskPath) + ' -TaskName ' + (ConvertTo-HomeCALiteral $TaskName) + ' -Confirm:$false -ErrorAction SilentlyContinue')
            }
            Write-Output "homeca-setup: backup in $($env:HOMECA_BACKUP_DIR) (undo with restore.ps1)"

            Set-Content -LiteralPath $TokenFile -Value '{{token}}'
            Set-Content -LiteralPath $CaCopy -Value @'
            {{rootCaPem.Replace("\r\n", "\n").Trim()}}
            '@
            $rootCa = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $CaCopy
            if ('{{(installRootCa ? 1 : 0)}}' -eq '1') {
                if (-not (Test-Path -LiteralPath ('Cert:\LocalMachine\Root\' + $rootCa.Thumbprint))) {
                    Add-HomeCARestoreStep ('Remove-Item -LiteralPath ' + (ConvertTo-HomeCALiteral ('Cert:\LocalMachine\Root\' + $rootCa.Thumbprint)) + ' -ErrorAction SilentlyContinue')
                    $store = New-Object Security.Cryptography.X509Certificates.X509Store ('Root', 'LocalMachine')
                    $store.Open('ReadWrite')
                    try { $store.Add($rootCa) } finally { $store.Close() }
                }
                Write-Output 'homeca-setup: root CA is in the Windows trust store (LocalMachine\Root)'
            }

            Set-Content -LiteralPath $AgentFile -Encoding UTF8 -Value @'
            {{agentScript.Replace("\r\n", "\n").TrimEnd('\n')}}
            '@

            # Use the Windows trust store when it already trusts HomeCA's TLS certificate, otherwise the HomeCA root CA.
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $settings = @{}
            $client = New-Object Net.WebClient
            $client.Headers['Authorization'] = 'Bearer {{token}}'
            try { [void] $client.DownloadString($ManifestUrl) }
            catch [Net.WebException] {
                if ($_.Exception.Status -eq [Net.WebExceptionStatus]::TrustFailure -or $_.Exception.Status -eq [Net.WebExceptionStatus]::SecureChannelFailure) { $settings.caFile = $CaCopy }
                else { [Console]::Error.WriteLine("homeca-setup: the device cannot reach HomeCA at {{url}}: $($_.Exception.Message)"); exit 21 }
            }
            finally { $client.Dispose() }
            Set-Content -LiteralPath $SettingsFile -Value ($settings | ConvertTo-Json)

            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$AgentFile`""
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes({{minute}}) -RepetitionInterval (New-TimeSpan -Hours 1)
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
            Register-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Description 'HomeCA deployment agent for {{Ascii(target.Name).Replace("'", "''")}}' -Force | Out-Null
            Write-Output "homeca-setup: scheduled task $TaskPath$TaskName runs the agent hourly as SYSTEM"

            Write-Output 'homeca-setup: running the agent once'
            & ([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $AgentFile
            exit $LASTEXITCODE

            """.Replace("\r\n", "\n");
    }

    /// <summary>Printable ASCII without quotes, for comments and task descriptions.</summary>
    private static string Ascii(string value) => new(value.Where(character => character is >= ' ' and <= '~' and not '"' and not '`' and not '$').ToArray());
}
