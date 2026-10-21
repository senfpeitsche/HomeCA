namespace HomeCA.Service.Deployments;

/// <summary>
/// Renders the POSIX shell agent a device runs to fetch its current certificate. The agent compares the
/// manifest fingerprint with the installed one, swaps the files atomically and runs the profile script.
/// </summary>
public static class DeploymentAgentScript
{
    /// <summary>Files a target may fetch; everything else in the export directory stays private.</summary>
    public static readonly IReadOnlyList<string> CertificateFiles = ["certificate.pem", "key.pem", "chain.pem", "fullchain.pem", "bundle.pem"];

    public static string ManifestUrl(string? baseUrl, string targetId) => $"{Normalize(baseUrl)}/api/v1/deploy/{targetId}/manifest";

    public static string TokenFile(string targetId) => $"/etc/homeca-agent/{targetId}.token";

    public static string EnvFile(string targetId) => $"/etc/homeca-agent/{targetId}.env";

    public static string AgentPath(string targetId) => $"/usr/local/bin/homeca-agent-{ShortId(targetId)}";

    /// <summary>Directory on the device that holds one backup per change, each with files.txt and restore.sh.</summary>
    public static string BackupRoot(string targetId) => $"/var/backups/homeca/{ShortId(targetId)}";

    /// <summary>Number of backups the agent keeps per target; older ones are removed after each run.</summary>
    public const int KeptBackups = 10;

    internal static string ShortId(string targetId) => targetId[..Math.Min(8, targetId.Length)];

    public static string Render(string? baseUrl, DeploymentTarget target) => $$"""
        #!/bin/sh
        # HomeCA deployment agent for "{{Sanitize(target.Name)}}" ({{target.Id}}).
        # Fetches the current certificate when it changed and runs the profile script (install.sh) if present.
        # Usage: {{AgentPath(target.Id)}} [--force]
        set -eu

        HOMECA_URL="{{Normalize(baseUrl)}}"
        TARGET_ID="{{target.Id}}"
        TOKEN_FILE="${TOKEN_FILE:-/etc/homeca-agent/{{target.Id}}.token}"
        STATE_DIR="${STATE_DIR:-/var/lib/homeca-agent/{{target.Id}}}"
        # Written by the guided SSH setup, for example to set CA_FILE.
        ENV_FILE="${ENV_FILE:-{{EnvFile(target.Id)}}}"
        if [ -f "$ENV_FILE" ]; then . "$ENV_FILE"; fi
        # Set CA_FILE when the HomeCA root CA is not in the system trust store.
        CA_FILE="${CA_FILE:-}"
        # Set RUN_SCRIPT=0 to only download the files.
        RUN_SCRIPT="${RUN_SCRIPT:-1}"
        # The profile script saves every file it replaces below BACKUP_ROOT; the newest KEEP_BACKUPS are kept.
        BACKUP_ROOT="${BACKUP_ROOT:-{{BackupRoot(target.Id)}}}"
        KEEP_BACKUPS="${KEEP_BACKUPS:-{{KeptBackups}}}"

        token=$(cat "$TOKEN_FILE")
        fetch() {
            if [ -n "$CA_FILE" ]; then
                curl -fsS --cacert "$CA_FILE" -H "Authorization: Bearer $token" -o "$2" "$HOMECA_URL/api/v1/deploy/$TARGET_ID/$1"
            else
                curl -fsS -H "Authorization: Bearer $token" -o "$2" "$HOMECA_URL/api/v1/deploy/$TARGET_ID/$1"
            fi
        }

        umask 077
        mkdir -p "$STATE_DIR"
        # The timer and a push from HomeCA may start the agent at the same time; only one run proceeds.
        LOCK_DIR="$STATE_DIR.lock"
        if ! mkdir "$LOCK_DIR" 2>/dev/null; then
            if [ -n "$(find "$LOCK_DIR" -maxdepth 0 -mmin +30 2>/dev/null)" ]; then
                rm -rf "$LOCK_DIR"
                mkdir "$LOCK_DIR"
            else
                echo "homeca-agent: another run is in progress" >&2
                exit 75
            fi
        fi
        incoming=$(mktemp -d "$STATE_DIR/.incoming.XXXXXX")
        trap 'rm -rf "$incoming" "$LOCK_DIR"' EXIT
        # A signal (for example SIGHUP when an SSH push is cut off) exits through the EXIT trap, so the lock never stays behind.
        trap 'exit 1' HUP INT TERM

        fetch manifest "$incoming/manifest.json"
        fingerprint=$(sed -n 's/.*"fingerprint":"\([0-9a-f]*\)".*/\1/p' "$incoming/manifest.json")
        if [ -z "$fingerprint" ]; then
            echo "homeca-agent: the manifest has no fingerprint" >&2
            exit 1
        fi
        if [ "${1:-}" != "--force" ] && [ -f "$STATE_DIR/current/fingerprint" ] && [ "$(cat "$STATE_DIR/current/fingerprint")" = "$fingerprint" ]; then
            # Only a push over SSH reports this; cron would mail every line of output.
            if [ -n "${SSH_CONNECTION:-}" ]; then echo "homeca-agent: certificate $fingerprint is already installed"; fi
            exit 0
        fi

        for file in $(sed -n 's/.*"files":\[\([^]]*\)\].*/\1/p' "$incoming/manifest.json" | tr -d '"' | tr ',' ' '); do
            fetch "files/$file" "$incoming/$file"
        done
        if command -v openssl >/dev/null 2>&1; then
            actual=$(openssl x509 -in "$incoming/certificate.pem" -outform DER | openssl dgst -sha256 | sed 's/.*= *//')
            if [ "$actual" != "$fingerprint" ]; then
                echo "homeca-agent: downloaded certificate does not match the manifest" >&2
                exit 1
            fi
        fi
        printf '%s\n' "$fingerprint" > "$incoming/fingerprint"

        rm -rf "$STATE_DIR/previous"
        if [ -d "$STATE_DIR/current" ]; then mv "$STATE_DIR/current" "$STATE_DIR/previous"; fi
        mv "$incoming" "$STATE_DIR/current"
        trap 'rm -rf "$LOCK_DIR"' EXIT

        if [ "$RUN_SCRIPT" = "1" ] && [ -f "$STATE_DIR/current/install.sh" ]; then
            HOMECA_BACKUP_DIR="$BACKUP_ROOT/$(date +%Y%m%d-%H%M%S)-renewal"
            export HOMECA_BACKUP_DIR
            (cd "$STATE_DIR/current" && sh ./install.sh)
            if [ -f "$HOMECA_BACKUP_DIR/restore.sh" ]; then echo "homeca-agent: backup in $HOMECA_BACKUP_DIR (undo with restore.sh)"; fi
        fi
        if [ -d "$BACKUP_ROOT" ]; then
            count=$(ls -1 "$BACKUP_ROOT" | wc -l)
            if [ "$count" -gt "$KEEP_BACKUPS" ]; then
                ls -1 "$BACKUP_ROOT" | sort | head -n "$((count - KEEP_BACKUPS))" | while IFS= read -r old; do rm -rf "${BACKUP_ROOT:?}/$old"; done
            fi
        fi
        echo "homeca-agent: installed certificate $fingerprint"

        """.Replace("\r\n", "\n");

    /// <summary>One-time setup commands for a Linux target. The token appears here only right after it was created.</summary>
    public static string RenderInstallCommands(string? baseUrl, DeploymentTarget target, string token)
    {
        var agentPath = AgentPath(target.Id);
        var tokenFile = $"/etc/homeca-agent/{target.Id}.token";
        return $$"""
            sudo install -d -m 700 /etc/homeca-agent
            printf '%s\n' '{{token}}' | sudo tee {{tokenFile}} >/dev/null
            sudo chmod 600 {{tokenFile}}
            sudo curl -fsS -H "Authorization: Bearer {{token}}" -o {{agentPath}} {{Normalize(baseUrl)}}/api/v1/deploy/{{target.Id}}/agent.sh
            sudo chmod 755 {{agentPath}}
            sudo {{agentPath}}
            printf '%s\n' '{{Random.Shared.Next(0, 60)}} * * * * root {{agentPath}}' | sudo tee /etc/cron.d/homeca-agent-{{target.Id[..Math.Min(8, target.Id.Length)]}} >/dev/null
            """.Replace("\r\n", "\n");
    }

    internal static string Normalize(string? baseUrl) => string.IsNullOrWhiteSpace(baseUrl) ? "https://homeca.example" : baseUrl.Trim().TrimEnd('/');

    // The name only appears in a comment; strip anything that could end it.
    internal static string Sanitize(string value) => new(value.Where(character => !char.IsControl(character) && character != '"').ToArray());
}
