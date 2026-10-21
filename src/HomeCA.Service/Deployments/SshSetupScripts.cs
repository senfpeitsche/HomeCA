using System.Net;
using System.Net.Sockets;
using HomeCA.Service.Profiles;

namespace HomeCA.Service.Deployments;

/// <summary>
/// Scripts the guided SSH setup runs on a device: a read-only detection script and the setup script that installs
/// the deployment agent. Both are sent through standard input, so the target token never touches the device's disk
/// outside the token file.
/// </summary>
public static class SshSetupScripts
{
    /// <summary>
    /// Profile IDs in the order they are suggested. Specific appliances come before general web servers, because an
    /// appliance such as Proxmox often also has nginx installed.
    /// </summary>
    public static readonly IReadOnlyList<string> ServicePriority =
    [
        "proxmox", "vmware-esxi", "synology", "unraid", "truenas", "home-assistant", "unifi", "opnsense", "pfsense",
        "jellyfin", "teamcity", "portainer", "nginx-proxy-manager", "traefik", "caddy", "haproxy", "nextcloud-apache", "nginx", "iis", "rdp"
    ];

    /// <summary>Read-only script that prints one <c>key=value</c> pair per line. It changes nothing on the device.</summary>
    public static readonly string DetectionScript = """
        LC_ALL=C; export LC_ALL
        # Tools such as update-ca-certificates live in sbin, which is often missing from a regular user's PATH.
        PATH="$PATH:/usr/local/sbin:/usr/sbin:/sbin"; export PATH
        kv() { printf '%s=%s\n' "$1" "$(printf '%s' "$2" | tr -d '\r\n')"; }
        has() { command -v "$1" >/dev/null 2>&1; }
        kv uid "$(id -u 2>/dev/null)"
        kv user "$(id -un 2>/dev/null)"
        kv hostname "$(hostname 2>/dev/null || uname -n)"
        kv fqdn "$(hostname -f 2>/dev/null)"
        if [ -r /etc/os-release ]; then kv os "$(. /etc/os-release; printf '%s' "${PRETTY_NAME:-${NAME:-}}")"; fi
        kv kernel "$(uname -s 2>/dev/null)"
        kv arch "$(uname -m 2>/dev/null)"
        if has ip; then ip -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1
        elif hostname -I >/dev/null 2>&1; then hostname -I | tr ' ' '\n'
        elif has ifconfig; then ifconfig 2>/dev/null | awk '/inet /{print $2}' | sed 's/addr://'
        fi | while IFS= read -r address; do if [ -n "$address" ]; then kv address "$address"; fi; done
        for tool in curl wget openssl sudo systemctl update-ca-certificates update-ca-trust docker; do if has "$tool"; then kv tool "$tool"; fi; done
        if [ -d /run/systemd/system ] && has systemctl; then kv scheduler systemd; elif [ -d /etc/cron.d ]; then kv scheduler cron; else kv scheduler none; fi
        if has update-ca-certificates && [ -d /usr/local/share/ca-certificates ]; then kv truststore debian
        elif has update-ca-trust && [ -d /etc/pki/ca-trust/source/anchors ]; then kv truststore rhel
        else kv truststore none; fi
        if [ "$(id -u 2>/dev/null)" = 0 ]; then kv sudo root
        elif has sudo; then if sudo -n true >/dev/null 2>&1; then kv sudo nopasswd; else kv sudo password; fi
        else kv sudo none; fi
        if [ -d /etc/pve ]; then kv service proxmox; fi
        if [ "$(uname -s 2>/dev/null)" = VMkernel ]; then kv service vmware-esxi; fi
        if [ -f /etc/synoinfo.conf ]; then kv service synology; fi
        if [ -f /etc/unraid-version ]; then kv service unraid; fi
        if has midclt || grep -qi truenas /etc/version 2>/dev/null; then kv service truenas; fi
        if has ha || [ -d /usr/share/hassio ]; then kv service home-assistant; fi
        if [ -d /usr/lib/unifi ] || [ -d /var/lib/unifi ]; then kv service unifi; fi
        if [ -d /usr/local/opnsense ]; then kv service opnsense; fi
        if grep -qi pfsense /etc/platform 2>/dev/null; then kv service pfsense; fi
        if [ -d /etc/jellyfin ]; then kv service jellyfin; fi
        if [ -d /opt/teamcity ]; then kv service teamcity; fi
        containers=$(docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null || true)
        case "$containers" in *portainer*) kv service portainer ;; esac
        case "$containers" in *nginx-proxy-manager*) kv service nginx-proxy-manager ;; esac
        case "$containers" in *traefik*) kv service traefik ;; esac
        if has traefik || [ -d /etc/traefik ]; then kv service traefik; fi
        if has caddy || [ -f /etc/caddy/Caddyfile ]; then kv service caddy; fi
        if [ -d /etc/haproxy ]; then kv service haproxy; fi
        if { [ -d /var/www/nextcloud ] || [ -d /var/www/html/nextcloud ]; } && { has apache2 || has httpd; }; then kv service nextcloud-apache; fi
        if has nginx || [ -d /etc/nginx ]; then kv service nginx; fi
        exit 0

        """.Replace("\r\n", "\n");

    public static SshHostFacts ParseFacts(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var addresses = new List<string>();
        var tools = new List<string>();
        var services = new List<string>();
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0 || value.Length > 255) continue;
            switch (key)
            {
                case "address": if (IPAddress.TryParse(value, out _) && !addresses.Contains(value)) addresses.Add(value); break;
                case "tool": if (!tools.Contains(value)) tools.Add(value); break;
                case "service": if (!services.Contains(value)) services.Add(value); break;
                default: values[key] = value; break;
            }
        }
        string? Value(string key) => values.TryGetValue(key, out var value) ? value : null;
        var hostName = Value("hostname");
        var fqdn = Value("fqdn");
        if (fqdn is not null && (!fqdn.Contains('.') || Uri.CheckHostName(fqdn) != UriHostNameType.Dns)) fqdn = null;
        return new SshHostFacts(
            Value("user"), Value("uid") == "0", Value("sudo") ?? SshHostFacts.SudoNone, hostName, fqdn, addresses,
            Value("os"), Value("kernel"), Value("arch"), Value("scheduler") ?? SshHostFacts.SchedulerNone, Value("truststore") ?? "none",
            tools, services.OrderBy(Priority).ToList(), Value("platform") == SshHostFacts.Windows ? SshHostFacts.Windows : SshHostFacts.Linux);

        static int Priority(string service)
        {
            for (var index = 0; index < ServicePriority.Count; index++)
                if (ServicePriority[index] == service) return index;
            return int.MaxValue;
        }
    }

    /// <summary>Suggests profiles, certificate names and an existing certificate for the detected device.</summary>
    public static SshSetupSuggestion Suggest(SshHostFacts facts, string connectionHost, IReadOnlyList<TargetProfile> profiles, IReadOnlyList<(string Id, string Subject)> certificates)
    {
        var known = profiles.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profileIds = facts.Services.Where(known.Contains).ToList();
        var fallback = facts.IsWindows ? "windows-certstore" : "generic-tls";
        if (facts.IsWindows && known.Contains(fallback) && !profileIds.Contains(fallback)) profileIds.Add(fallback);
        if (profileIds.Count == 0 && known.Contains(fallback)) profileIds.Add(fallback);

        var dnsNames = new List<string>();
        void AddName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || Uri.CheckHostName(name) != UriHostNameType.Dns) return;
            var normalized = name.Trim().TrimEnd('.').ToLowerInvariant();
            if (normalized is "localhost" or "localhost.localdomain" || normalized.EndsWith(".localdomain", StringComparison.Ordinal)) return;
            if (!dnsNames.Contains(normalized)) dnsNames.Add(normalized);
        }
        AddName(facts.Fqdn);
        AddName(connectionHost);
        // A bare host name only helps when nothing better is known; certificates for single labels are rarely useful.
        if (dnsNames.Count == 0) AddName(facts.HostName);

        var ipAddresses = facts.Addresses
            .Where(address => IPAddress.TryParse(address, out var ip) && IsUsableAddress(ip))
            .ToList();
        if (IPAddress.TryParse(connectionHost, out var connectionIp) && IsUsableAddress(connectionIp) && !ipAddresses.Contains(connectionIp.ToString()))
            ipAddresses.Insert(0, connectionIp.ToString());

        string? certificateId = null;
        foreach (var candidate in dnsNames.Concat(ipAddresses))
        {
            var match = certificates.FirstOrDefault(certificate => certificate.Subject.Split(',').Any(part => part.Trim().Equals($"CN={candidate}", StringComparison.OrdinalIgnoreCase)));
            if (match.Id is not null) { certificateId = match.Id; break; }
        }

        var name = dnsNames.FirstOrDefault() ?? facts.HostName ?? connectionHost;
        return new SshSetupSuggestion(profileIds, dnsNames, ipAddresses, certificateId, name);
    }

    /// <summary>Comment that marks HomeCA's authorized_keys line for one target.</summary>
    public static string PushKeyComment(string targetId) => $"{SshPushKeyService.KeyComment}-{DeploymentAgentScript.ShortId(targetId)}";

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || address.IsIPv6Multicast)) return false;
        // Default Docker bridge networks are never what clients use to reach a device.
        var bytes = address.GetAddressBytes();
        return !(address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 172 && bytes[1] == 17);
    }

    /// <summary>
    /// Renders the setup script. It saves every file it replaces or creates, writes the token, the agent and a
    /// systemd timer or cron entry, optionally adds the root CA to the system trust store and runs the agent once.
    /// </summary>
    public static string RenderSetup(string baseUrl, DeploymentTarget target, string token, string agentScript, string rootCaPem, string rootCaFingerprint, bool installRootCa, int minute,
        string? pushPublicKey = null)
    {
        if (token.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new ArgumentException("The token contains unexpected characters.");
        if (agentScript.Contains("\nHOMECA_AGENT_EOF\n") || rootCaPem.Contains("HOMECA_CA_EOF")) throw new ArgumentException("A heredoc delimiter appears in the content.");
        var url = DeploymentAgentScript.Normalize(baseUrl);
        var shortId = DeploymentAgentScript.ShortId(target.Id);
        // The unit files are written through an unquoted heredoc, so characters the shell expands are dropped.
        var description = new string(DeploymentAgentScript.Sanitize(target.Name).Where(character => character is not ('$' or '`' or '\\')).ToArray()).Replace("%", "%%");
        var caName = $"homeca-root-{rootCaFingerprint[..Math.Min(12, rootCaFingerprint.Length)].ToLowerInvariant()}";
        var pushComment = PushKeyComment(target.Id);
        var pushLine = "";
        if (pushPublicKey is not null)
        {
            var parts = pushPublicKey.Trim().Split(' ');
            if (parts.Length < 2 || parts[0] != "ecdsa-sha2-nistp256" || parts[1].Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=')))
                throw new ArgumentException("The push key is not an OpenSSH ECDSA public key.");
            // The key may only start this target's agent: no shell, no terminal and no forwarding.
            pushLine = $"command=\"{DeploymentAgentScript.AgentPath(target.Id)}\",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding {parts[0]} {parts[1]} {pushComment}";
        }
        return $$"""
            #!/bin/sh
            # HomeCA guided setup for "{{DeploymentAgentScript.Sanitize(target.Name)}}" ({{target.Id}}).
            # Every file this script replaces or creates is saved first; restore.sh in the backup directory undoes it.
            set -eu
            umask 077
            PATH="$PATH:/usr/local/sbin:/usr/sbin:/sbin"; export PATH
            HOMECA_BACKUP_DIR="{{DeploymentAgentScript.BackupRoot(target.Id)}}/$(date +%Y%m%d-%H%M%S)-setup"
            {{ProfileScripts.ShellBackupHelpers}}
            TOKEN_FILE="{{DeploymentAgentScript.TokenFile(target.Id)}}"
            ENV_FILE="{{DeploymentAgentScript.EnvFile(target.Id)}}"
            CA_COPY=/etc/homeca-agent/{{caName}}.pem
            AGENT="{{DeploymentAgentScript.AgentPath(target.Id)}}"
            UNIT=homeca-agent-{{shortId}}
            MANIFEST_URL="{{url}}/api/v1/deploy/{{target.Id}}/manifest"
            TRUST_UPDATE=""
            # The agent keeps copies of the certificate and key here; undoing a first setup removes them again.
            STATE_DIR="/var/lib/homeca-agent/{{target.Id}}"
            if [ -e "$STATE_DIR" ]; then NEW_STATE=""; else NEW_STATE=1; fi
            if [ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1; then SCHEDULER=systemd
            elif [ -d /etc/cron.d ]; then SCHEDULER=cron
            else SCHEDULER=none; fi
            if ! command -v curl >/dev/null 2>&1; then echo "homeca-setup: curl is required on the device" >&2; exit 20; fi

            # A timer from an earlier setup must run again after restore.sh put its files back.
            if [ -f "/etc/systemd/system/$UNIT.timer" ]; then TIMER_EXISTED=1; else TIMER_EXISTED=""; fi
            homeca_backup "$TOKEN_FILE" "$ENV_FILE" "$CA_COPY" "$AGENT" "/etc/systemd/system/$UNIT.service" "/etc/systemd/system/$UNIT.timer" "/etc/cron.d/$UNIT"
            printf 'if command -v systemctl >/dev/null 2>&1; then systemctl disable --now %s.timer >/dev/null 2>&1 || true; fi\n' "$UNIT" > "$HOMECA_BACKUP_DIR/before-restore.sh"
            echo "homeca-setup: backup in $HOMECA_BACKUP_DIR (undo with restore.sh)"

            install -d -m 700 /etc/homeca-agent
            printf '%s\n' '{{token}}' > "$TOKEN_FILE"
            chmod 600 "$TOKEN_FILE"
            cat > "$CA_COPY" <<'HOMECA_CA_EOF'
            {{rootCaPem.Replace("\r\n", "\n").Trim()}}
            HOMECA_CA_EOF
            chmod 644 "$CA_COPY"

            if [ "{{(installRootCa ? 1 : 0)}}" = 1 ]; then
                if command -v update-ca-certificates >/dev/null 2>&1 && [ -d /usr/local/share/ca-certificates ]; then
                    homeca_backup /usr/local/share/ca-certificates/{{caName}}.crt
                    command cp "$CA_COPY" /usr/local/share/ca-certificates/{{caName}}.crt
                    chmod 644 /usr/local/share/ca-certificates/{{caName}}.crt
                    TRUST_UPDATE="update-ca-certificates"
                elif command -v update-ca-trust >/dev/null 2>&1 && [ -d /etc/pki/ca-trust/source/anchors ]; then
                    homeca_backup /etc/pki/ca-trust/source/anchors/{{caName}}.pem
                    command cp "$CA_COPY" /etc/pki/ca-trust/source/anchors/{{caName}}.pem
                    chmod 644 /etc/pki/ca-trust/source/anchors/{{caName}}.pem
                    TRUST_UPDATE="update-ca-trust extract"
                else
                    echo "homeca-setup: warning: no supported system trust store; the agent uses $CA_COPY instead"
                fi
                if [ -n "$TRUST_UPDATE" ]; then $TRUST_UPDATE >/dev/null; echo "homeca-setup: root CA added to the system trust store"; fi
            fi

            cat > "$AGENT" <<'HOMECA_AGENT_EOF'
            {{agentScript.Replace("\r\n", "\n").TrimEnd('\n')}}
            HOMECA_AGENT_EOF
            chmod 755 "$AGENT"

            # Use the system trust store when it already trusts HomeCA's TLS certificate, otherwise the HomeCA root CA.
            : > "$ENV_FILE"
            chmod 600 "$ENV_FILE"
            if ! curl -fsS -o /dev/null -H "Authorization: Bearer $(cat "$TOKEN_FILE")" "$MANIFEST_URL" 2>/dev/null; then
                if curl -fsS --cacert "$CA_COPY" -o /dev/null -H "Authorization: Bearer $(cat "$TOKEN_FILE")" "$MANIFEST_URL"; then
                    printf 'CA_FILE="${CA_FILE:-%s}"\n' "$CA_COPY" > "$ENV_FILE"
                else
                    echo "homeca-setup: the device cannot reach HomeCA at {{url}}" >&2
                    exit 21
                fi
            fi

            case "$SCHEDULER" in
                systemd)
                    cat > "/etc/systemd/system/$UNIT.service" <<HOMECA_UNIT_EOF
            [Unit]
            Description=HomeCA deployment agent for {{description}}
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=oneshot
            ExecStart=$AGENT
            HOMECA_UNIT_EOF
                    cat > "/etc/systemd/system/$UNIT.timer" <<HOMECA_UNIT_EOF
            [Unit]
            Description=Hourly HomeCA deployment agent run for {{description}}

            [Timer]
            OnCalendar=*-*-* *:{{minute:00}}:00
            RandomizedDelaySec=120
            Persistent=true

            [Install]
            WantedBy=timers.target
            HOMECA_UNIT_EOF
                    chmod 644 "/etc/systemd/system/$UNIT.service" "/etc/systemd/system/$UNIT.timer"
                    rm -f "/etc/cron.d/$UNIT"
                    systemctl daemon-reload
                    systemctl enable --now "$UNIT.timer" >/dev/null
                    echo "homeca-setup: systemd timer $UNIT.timer runs the agent hourly"
                    ;;
                cron)
                    printf '%s * * * * root %s\n' '{{minute}}' "$AGENT" > "/etc/cron.d/$UNIT"
                    chmod 644 "/etc/cron.d/$UNIT"
                    echo "homeca-setup: /etc/cron.d/$UNIT runs the agent hourly"
                    ;;
                *)
                    echo "homeca-setup: warning: neither systemd nor /etc/cron.d is available; run $AGENT regularly yourself"
                    ;;
            esac

            # Push: only HomeCA's own line in root's authorized_keys changes; restore.sh removes just that line.
            PUSH_LINE='{{pushLine}}'
            ROOT_HOME=$(awk -F: '$1 == "root" { print $6 }' /etc/passwd 2>/dev/null)
            AUTHORIZED_KEYS="${ROOT_HOME:-/root}/.ssh/authorized_keys"
            if [ -d /etc/ssh/keys-root ]; then AUTHORIZED_KEYS=/etc/ssh/keys-root/authorized_keys; fi
            if [ -n "$PUSH_LINE" ] || grep -q ' {{pushComment}}$' "$AUTHORIZED_KEYS" 2>/dev/null; then
                if [ -f "$AUTHORIZED_KEYS" ]; then
                    mkdir -p "$HOMECA_BACKUP_DIR/files$(dirname "$AUTHORIZED_KEYS")"
                    command cp -p "$AUTHORIZED_KEYS" "$HOMECA_BACKUP_DIR/files$AUTHORIZED_KEYS"
                    printf '%s\n' "$AUTHORIZED_KEYS" >> "$HOMECA_BACKUP_DIR/files.txt"
                fi
                printf "if [ -f %s ]; then sed -i '/ {{pushComment}}\$/d' %s; fi\n" "$(homeca_quote "$AUTHORIZED_KEYS")" "$(homeca_quote "$AUTHORIZED_KEYS")" >> "$HOMECA_BACKUP_DIR/restore.sh"
                mkdir -p "$(dirname "$AUTHORIZED_KEYS")"
                chmod 700 "$(dirname "$AUTHORIZED_KEYS")"
                if [ -f "$AUTHORIZED_KEYS" ]; then grep -v ' {{pushComment}}$' "$AUTHORIZED_KEYS" > "$AUTHORIZED_KEYS.homeca" || true; else : > "$AUTHORIZED_KEYS.homeca"; fi
                if [ -n "$PUSH_LINE" ]; then printf '%s\n' "$PUSH_LINE" >> "$AUTHORIZED_KEYS.homeca"; fi
                chmod 600 "$AUTHORIZED_KEYS.homeca"
                mv "$AUTHORIZED_KEYS.homeca" "$AUTHORIZED_KEYS"
                if command -v restorecon >/dev/null 2>&1; then restorecon "$AUTHORIZED_KEYS" 2>/dev/null || true; fi
                if [ -n "$PUSH_LINE" ]; then
                    echo "homeca-setup: push key added to $AUTHORIZED_KEYS; it can only start the agent"
                    ROOT_LOGIN=$(sshd -T 2>/dev/null | awk '$1 == "permitrootlogin" { print $2 }')
                    if [ "$ROOT_LOGIN" = no ]; then echo "homeca-setup: warning: sshd does not allow root logins (PermitRootLogin no); push stays off"; fi
                else
                    echo "homeca-setup: push key removed from $AUTHORIZED_KEYS"
                fi
            fi

            {
                echo 'if [ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1; then systemctl daemon-reload; fi'
                if [ -n "$TIMER_EXISTED" ]; then echo "systemctl enable --now $UNIT.timer >/dev/null 2>&1 || true"; fi
                if [ -n "$TRUST_UPDATE" ]; then echo "$TRUST_UPDATE >/dev/null"; fi
                if [ -n "$NEW_STATE" ]; then echo "rm -rf -- $(homeca_quote "$STATE_DIR")"; fi
                echo 'rmdir /etc/homeca-agent 2>/dev/null || true'
            } >> "$HOMECA_BACKUP_DIR/restore.sh"

            echo "homeca-setup: running the agent once"
            "$AGENT"

            """.Replace("\r\n", "\n");
    }
}

/// <summary>What the read-only detection script found on a device.</summary>
public sealed record SshHostFacts(
    string? UserName, bool IsRoot, string Sudo, string? HostName, string? Fqdn, IReadOnlyList<string> Addresses,
    string? OperatingSystem, string? Kernel, string? Architecture, string Scheduler, string TrustStore,
    IReadOnlyList<string> Tools, IReadOnlyList<string> Services, string Platform = SshHostFacts.Linux)
{
    public const string Linux = "linux";
    public const string Windows = "windows";

    public const string SudoRoot = "root";
    public const string SudoWithoutPassword = "nopasswd";
    public const string SudoWithPassword = "password";
    public const string SudoNone = "none";
    public const string SchedulerNone = "none";

    public bool HasCurl => Tools.Contains("curl");

    public bool IsWindows => Platform == Windows;
}

public sealed record SshSetupSuggestion(IReadOnlyList<string> ProfileIds, IReadOnlyList<string> DnsNames, IReadOnlyList<string> IpAddresses, string? CertificateId, string Name);
