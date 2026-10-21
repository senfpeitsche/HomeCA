using HomeCA.Service.Deployments;
using HomeCA.Service.Automation;
using HomeCA.Service.Notifications;
using HomeCA.Service.Resources;
using HomeCA.Service.Infrastructure;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using HomeCA.Service.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class SshSetupTests : IDisposable
{
    private const string HostKey = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    private const string SshPassword = "ssh-password-42";
    private readonly TestFixture _fixture = new();

    private const string ProxmoxDetection = """
        uid=1000
        user=admin
        hostname=pve01
        fqdn=pve01.lab.test
        os=Debian GNU/Linux 12 (bookworm)
        kernel=Linux
        arch=x86_64
        address=192.168.10.5
        address=172.17.0.1
        address=fe80::1
        tool=curl
        tool=sudo
        scheduler=systemd
        truststore=debian
        sudo=password
        service=nginx
        service=proxmox
        """;

    [Fact]
    public void Detection_output_is_parsed_and_appliances_come_before_web_servers()
    {
        var facts = SshSetupScripts.ParseFacts(ProxmoxDetection);

        Assert.Equal("admin", facts.UserName);
        Assert.False(facts.IsRoot);
        Assert.Equal(SshHostFacts.SudoWithPassword, facts.Sudo);
        Assert.Equal("pve01.lab.test", facts.Fqdn);
        Assert.Equal(["192.168.10.5", "172.17.0.1", "fe80::1"], facts.Addresses);
        Assert.Equal(["proxmox", "nginx"], facts.Services);
        Assert.Equal("systemd", facts.Scheduler);
        Assert.True(facts.HasCurl);
    }

    [Fact]
    public void Single_label_fqdn_and_garbage_lines_are_ignored()
    {
        var facts = SshSetupScripts.ParseFacts("fqdn=pve01\naddress=not-an-ip\nnonsense\n=x\nsudo=root\n");

        Assert.Null(facts.Fqdn);
        Assert.Empty(facts.Addresses);
        Assert.Equal(SshHostFacts.SudoRoot, facts.Sudo);
        Assert.False(facts.HasCurl);
    }

    [Fact]
    public void Suggestion_picks_profile_names_usable_addresses_and_a_matching_certificate()
    {
        var facts = SshSetupScripts.ParseFacts(ProxmoxDetection);
        var profiles = new[] { Profile("proxmox"), Profile("nginx"), Profile("generic-tls") };

        var suggestion = SshSetupScripts.Suggest(facts, "192.168.10.5", profiles, [("aa01", "CN=other.lab.test"), ("bb02", "CN=pve01.lab.test")]);

        Assert.Equal(["proxmox", "nginx"], suggestion.ProfileIds);
        Assert.Equal(["pve01.lab.test"], suggestion.DnsNames);
        Assert.Equal(["192.168.10.5"], suggestion.IpAddresses);
        Assert.Equal("bb02", suggestion.CertificateId);
        Assert.Equal("pve01.lab.test", suggestion.Name);
    }

    [Fact]
    public void Suggestion_falls_back_to_the_generic_profile()
    {
        var facts = SshSetupScripts.ParseFacts("hostname=box\nkernel=Linux\n");

        var suggestion = SshSetupScripts.Suggest(facts, "box.lab.test", [Profile("generic-tls")], []);

        Assert.Equal(["generic-tls"], suggestion.ProfileIds);
        Assert.Equal(["box.lab.test"], suggestion.DnsNames);
        Assert.Null(suggestion.CertificateId);
    }

    [Fact]
    public void Credentials_never_appear_in_ToString_or_cleaned_output()
    {
        var credentials = new SshCredentials("pve01", 22, "admin", SshPassword, "-----BEGIN KEY-----", "phrase-99", "sudo-secret-7");

        Assert.Equal("admin@pve01:22", credentials.ToString());
        var cleaned = SshSetupService.Clean($"ok {SshPassword} phrase-99 sudo-secret-7 token-abc", credentials, "token-abc");
        Assert.Equal("ok *** *** *** ***", cleaned);
    }

    [Fact]
    public void Setup_script_backs_up_before_writing_and_escapes_the_target_name()
    {
        var target = new DeploymentTarget("0123456789abcdef", "pve $(reboot) `id` 100%", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var script = SshSetupScripts.RenderSetup("https://ca.lab.test/", target, "Tok-en_123", "#!/bin/sh\necho agent\n", "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", "ABCDEF0123456789", true, 7);

        Assert.DoesNotContain("\r", script);
        Assert.Contains("printf '%s\\n' 'Tok-en_123' > \"$TOKEN_FILE\"", script);
        Assert.True(script.IndexOf("homeca_backup \"$TOKEN_FILE\"", StringComparison.Ordinal) < script.IndexOf("> \"$TOKEN_FILE\"", StringComparison.Ordinal));
        Assert.Contains("HOMECA_BACKUP_DIR=\"/var/backups/homeca/01234567/", script);
        Assert.Contains("homeca_backup() {", script);
        Assert.Contains("Description=HomeCA deployment agent for pve (reboot) id 100%%", script);
        Assert.Contains("OnCalendar=*-*-* *:07:00", script);
        Assert.Contains("/usr/local/share/ca-certificates/homeca-root-abcdef012345.crt", script);
        Assert.Contains("MANIFEST_URL=\"https://ca.lab.test/api/v1/deploy/0123456789abcdef/manifest\"", script);
    }

    [Theory]
    [InlineData("tok'en")]
    [InlineData("tok en")]
    [InlineData("tok$en")]
    public void Setup_script_rejects_unexpected_token_characters(string token)
    {
        var target = new DeploymentTarget("0123456789abcdef", "pve", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        Assert.Throws<ArgumentException>(() => SshSetupScripts.RenderSetup("https://ca.lab.test", target, token, "#!/bin/sh\n", "PEM", "AB", false, 0));
    }

    [Fact]
    public void Shell_profile_scripts_save_files_before_cp_and_install_write_them()
    {
        var profile = new TargetProfile("nginx", "1.1", "nginx", "Web", "ECC", ["PEM"], new ProfileValidation(true, true, 730), "Docs", "cp fullchain.pem /etc/nginx/ssl/fullchain.pem", ProfileScripts.Shell);

        var script = ProfileScripts.Render(profile, ProfileScripts.Shell);

        Assert.True(script.IndexOf("cp() { homeca_backup_destinations cp", StringComparison.Ordinal) < script.IndexOf("cp fullchain.pem", StringComparison.Ordinal));
        Assert.Contains("install() { homeca_backup_destinations install", script);
        Assert.Contains("HOMECA_BACKUP_DIR=\"${HOMECA_BACKUP_DIR:-/var/backups/homeca/manual/", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void Built_in_profiles_that_write_without_cp_or_install_call_homeca_backup()
    {
        var seed = System.Text.Json.JsonSerializer.Deserialize<List<TargetProfile>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "profiles.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        foreach (var profile in seed.Where(profile => ProfileScripts.Resolve(profile) == ProfileScripts.Shell))
        {
            var writes = profile.RenewalScriptTemplate.Split('\n').Where(line => !line.TrimStart().StartsWith('#'))
                .Any(line => line.Contains(" > ") || line.Contains("-out /") || line.Contains("-destkeystore"));
            if (writes) Assert.Contains("homeca_backup ", profile.RenewalScriptTemplate);
        }
    }

    [Fact]
    public async Task Unedited_profiles_from_before_backups_get_the_backup_call()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var seed = System.Text.Json.JsonSerializer.Deserialize<List<TargetProfile>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "profiles.json")), json)!;
        var current = seed.Single(profile => profile.Id == "jellyfin");
        var previous = current with { Version = "1.0", RenewalScriptTemplate = current.RenewalScriptTemplate.Replace("homeca_backup /etc/jellyfin/jellyfin.pfx\n", "") };
        var edited = seed.Single(profile => profile.Id == "unraid") with { RenewalScriptTemplate = "cat certificate.pem > /tmp/x" };
        var path = Path.Combine(_fixture.RootPath, "profiles", "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new[] { previous, edited }, json));

        var profiles = await new TargetProfileRegistry(_fixture.CreateStorage()).ListAsync(CancellationToken.None);

        Assert.StartsWith("homeca_backup /etc/jellyfin/jellyfin.pfx\n", Assert.Single(profiles, profile => profile.Id == "jellyfin").RenewalScriptTemplate);
        Assert.Equal("cat certificate.pem > /tmp/x", Assert.Single(profiles, profile => profile.Id == "unraid").RenewalScriptTemplate);
    }

    [Fact]
    public void Agent_keeps_backups_per_target_and_reads_the_setup_environment()
    {
        var target = new DeploymentTarget("0123456789abcdef", "pve", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var script = DeploymentAgentScript.Render("https://ca.lab.test", target);

        Assert.Contains("BACKUP_ROOT=\"${BACKUP_ROOT:-/var/backups/homeca/01234567}\"", script);
        Assert.Contains("KEEP_BACKUPS=\"${KEEP_BACKUPS:-10}\"", script);
        Assert.Contains("export HOMECA_BACKUP_DIR", script);
        Assert.Contains("ENV_FILE=\"${ENV_FILE:-/etc/homeca-agent/0123456789abcdef.env}\"", script);
    }

    [Fact]
    public async Task Unknown_host_key_stops_before_anything_changes()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();

        var result = await service.InstallAsync(InstallRequest(certificateId, hostKey: null), "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.HostKeyUnknown, result.Status);
        Assert.Equal(HostKey, result.HostKey!.Fingerprint);
        Assert.Empty(await targets.ListAsync(CancellationToken.None));
        Assert.Empty(ssh.Commands);
    }

    [Fact]
    public async Task Guided_setup_creates_the_target_with_a_working_token_and_hides_secrets()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();

        var result = await service.InstallAsync(InstallRequest(certificateId), "https://ignored.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.Ok, result.Status);
        Assert.Equal(0, result.ExitCode);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        Assert.Equal(HostKey, target.Ssh!.HostKeyFingerprint);
        Assert.Equal("admin", target.Ssh.UserName);
        Assert.Equal(["sh -s", "sudo -k -S -p '' true", "sudo -k -S -p '' sh -s"], ssh.Commands.Select(command => command.Command));
        var setup = ssh.Commands[^1];
        Assert.StartsWith(SshPassword + "\n#!/bin/sh\n", setup.Input);
        var token = TokenFrom(setup.Input!);
        Assert.NotNull(await targets.AuthenticateAsync(target.Id, token, CancellationToken.None));
        Assert.Contains("https://ca.lab.test/api/v1/deploy/", setup.Input);
        Assert.DoesNotContain(token, result.Output);
        Assert.DoesNotContain(SshPassword, result.Output);
        var stored = await File.ReadAllTextAsync(Path.Combine(_fixture.RootPath, "state", "deployment-targets.json"));
        Assert.DoesNotContain(SshPassword, stored);
        Assert.DoesNotContain(token, stored);
        var audit = await File.ReadAllTextAsync(Path.Combine(_fixture.RootPath, "audit", "events.ndjson"));
        Assert.Contains("deployment.ssh_setup", audit);
        Assert.DoesNotContain(SshPassword, audit);
    }

    [Fact]
    public async Task Wrong_sudo_password_creates_no_target()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        ssh.SudoAccepts = false;

        var result = await service.InstallAsync(InstallRequest(certificateId), "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.SudoFailed, result.Status);
        Assert.Empty(await targets.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(ssh.Commands, command => command.Command.EndsWith("sh -s", StringComparison.Ordinal) && command.Command.StartsWith("sudo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changed_host_key_of_a_set_up_target_is_refused_until_replaced()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        await service.InstallAsync(InstallRequest(certificateId), "https://ca.lab.test", CancellationToken.None);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        ssh.PresentedHostKey = "SHA256:reinstalled0000000000000000000000000000000";

        // The request repeats the old fingerprint and even claims the new one; the stored key still wins.
        var refused = await service.DetectAsync(new SshDetectRequest(Connection(ssh.PresentedHostKey), target.Id), CancellationToken.None);
        var replaced = await service.DetectAsync(new SshDetectRequest(Connection(ssh.PresentedHostKey) with { ReplaceHostKey = true }, target.Id), CancellationToken.None);

        Assert.Equal(SshSetupResult.HostKeyChanged, refused.Status);
        Assert.Equal(HostKey, refused.ExpectedHostKey);
        Assert.Equal(ssh.PresentedHostKey, refused.HostKey!.Fingerprint);
        Assert.Equal(SshSetupResult.Ok, replaced.Status);
    }

    [Fact]
    public async Task Setting_up_an_existing_target_again_rotates_its_token()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        await service.InstallAsync(InstallRequest(certificateId), "https://ca.lab.test", CancellationToken.None);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        var firstToken = TokenFrom(ssh.Commands[^1].Input!);

        var again = await service.InstallAsync(new SshInstallRequest(Connection(HostKey), target.Id, HomeCaUrl: "https://ca.lab.test"), "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.Ok, again.Status);
        Assert.Null(await targets.AuthenticateAsync(target.Id, firstToken, CancellationToken.None));
        Assert.NotNull(await targets.AuthenticateAsync(target.Id, TokenFrom(ssh.Commands[^1].Input!), CancellationToken.None));
    }

    [Fact]
    public async Task Loopback_HomeCA_url_is_rejected()
    {
        var (service, _, _, certificateId) = await CreateServiceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.InstallAsync(InstallRequest(certificateId) with { HomeCaUrl = "http://localhost:5152" }, "http://localhost:5152", CancellationToken.None));
    }

    [Fact]
    public async Task Detection_suggests_profiles_without_changing_anything()
    {
        var (service, targets, ssh, _) = await CreateServiceAsync();

        var result = await service.DetectAsync(new SshDetectRequest(Connection(HostKey)), CancellationToken.None);

        Assert.Equal(SshSetupResult.Ok, result.Status);
        Assert.Equal("proxmox", result.Suggestion!.ProfileIds[0]);
        Assert.Equal(["sh -s"], ssh.Commands.Select(command => command.Command));
        Assert.Empty(await targets.ListAsync(CancellationToken.None));
    }

    private static string WindowsTokenFrom(string script) =>
        script.Split('\n').First(line => line.StartsWith("Set-Content -LiteralPath $TokenFile -Value '", StringComparison.Ordinal)).Split('\'')[1];

    private static string TokenFrom(string script)
    {
        var line = script.Split('\n').First(text => text.StartsWith("printf '%s\\n' '", StringComparison.Ordinal) && text.EndsWith("> \"$TOKEN_FILE\"", StringComparison.Ordinal));
        return line.Split('\'')[3];
    }

    private static SshCredentials Connection(string? hostKey) => new("pve01.lab.test", 22, "admin", SshPassword, HostKeyFingerprint: hostKey);

    private static SshInstallRequest InstallRequest(string certificateId, string? hostKey = HostKey) =>
        new(Connection(hostKey), Name: "pve01", CertificateId: certificateId, HomeCaUrl: "https://ca.lab.test");

    private static TargetProfile Profile(string id) => new(id, "1", id, "Purpose", "ECC", ["PEM"], new ProfileValidation(true, true, 730), "Docs", "", ProfileScripts.Shell);

    private async Task<(SshSetupService Service, DeploymentTargetRegistry Targets, FakeSshClientFactory Ssh, string CertificateId)> CreateServiceAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var profiles = new TargetProfileRegistry(storage);
        var deployments = new DeploymentPackageService(profiles, NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        var issued = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["pve01.lab.test"], [], TargetProfileId: "proxmox"), CancellationToken.None);
        var targets = new DeploymentTargetRegistry(storage, NullLogger<DeploymentTargetRegistry>.Instance);
        var probes = new DeploymentProbeService(storage, targets, NullLogger<DeploymentProbeService>.Instance);
        var administration = new LocalAdministrationService(storage, new TestHostEnvironment(), NullLogger<LocalAdministrationService>.Instance);
        var ssh = new FakeSshClientFactory();
        _pushKeys = new SshPushKeyService(storage, NullLogger<SshPushKeyService>.Instance);
        _notifications = new DeploymentTargetTests.RecordingHandler();
        _pushes = new DeploymentPushService(ssh, _pushKeys, targets, probes, CreateNotifications(storage, _notifications), NullLogger<DeploymentPushService>.Instance) { BusyRetryDelay = TimeSpan.Zero };
        var service = new SshSetupService(ssh, targets, profiles, certificates, authorities, probes, administration, _pushKeys, _pushes, storage, NullLogger<SshSetupService>.Instance, _progress);
        return (service, targets, ssh, issued.Id);
    }

    private readonly SshSetupProgress _progress = new();
    private SshPushKeyService? _pushKeys;
    private DeploymentPushService? _pushes;
    private DeploymentTargetTests.RecordingHandler? _notifications;

    /// <summary>Notification service with one webhook channel that records every message.</summary>
    internal static NotificationService CreateNotifications(HomeCaStorage storage, HttpMessageHandler handler)
    {
        var registry = new NotificationChannelRegistry(storage);
        registry.SaveAsync(new SaveNotificationSettingsRequest("en", [new(null, "webhook", "Recorder", "https://recorder.lab.test/hook")]), CancellationToken.None).GetAwaiter().GetResult();
        var factory = new DeploymentTargetTests.SingleClientFactory(handler);
        var mail = new RenewalMailNotificationService(new RenewalNotificationSettingsRegistry(storage), factory, NullLogger<RenewalMailNotificationService>.Instance);
        var localizer = new StringLocalizer<UiText>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        return new NotificationService(registry, mail, factory, localizer, NullLogger<NotificationService>.Instance);
    }

    [Fact]
    public async Task Push_key_is_stored_encrypted_and_loads_in_the_ssh_client()
    {
        var storage = _fixture.CreateStorage();
        var keys = new SshPushKeyService(storage, NullLogger<SshPushKeyService>.Instance);

        var publicKey = await keys.GetPublicKeyAsync(CancellationToken.None);
        var again = await new SshPushKeyService(storage, NullLogger<SshPushKeyService>.Instance).GetPublicKeyAsync(CancellationToken.None);
        var pem = await keys.GetPrivateKeyPemAsync(CancellationToken.None);

        Assert.Equal(publicKey, again);
        Assert.Matches("^ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTY[A-Za-z0-9+/=]+ homeca-push$", publicKey);
        var stored = await File.ReadAllTextAsync(Path.Combine(_fixture.RootPath, "state", "ssh-push-key.pem"));
        Assert.Contains("BEGIN ENCRYPTED PRIVATE KEY", stored);
        using var keyStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(pem));
        using var keyFile = new Renci.SshNet.PrivateKeyFile(keyStream);
        Assert.Equal("ecdsa-sha2-nistp256", keyFile.Key.ToString());
    }

    [Fact]
    public void Push_line_only_allows_the_agent()
    {
        var target = new DeploymentTarget("0123456789abcdef", "pve", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var script = SshSetupScripts.RenderSetup("https://ca.lab.test", target, "token", "#!/bin/sh\n", "PEM", "AB", false, 0, "ecdsa-sha2-nistp256 AAAAE2Vj+/= homeca-push");
        var withoutPush = SshSetupScripts.RenderSetup("https://ca.lab.test", target, "token", "#!/bin/sh\n", "PEM", "AB", false, 0);

        Assert.Contains("PUSH_LINE='command=\"/usr/local/bin/homeca-agent-01234567\",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding ecdsa-sha2-nistp256 AAAAE2Vj+/= homeca-push-01234567'", script);
        Assert.Contains("sed -i '/ homeca-push-01234567\\$/d'", script);
        Assert.True(script.IndexOf("command cp -p \"$AUTHORIZED_KEYS\"", StringComparison.Ordinal) < script.IndexOf("mv \"$AUTHORIZED_KEYS.homeca\"", StringComparison.Ordinal));
        Assert.Contains("PUSH_LINE=''", withoutPush);
        Assert.Throws<ArgumentException>(() => SshSetupScripts.RenderSetup("https://ca.lab.test", target, "token", "#!/bin/sh\n", "PEM", "AB", false, 0, "ssh-rsa AAAA' ; reboot"));
    }

    [Fact]
    public async Task Setup_with_push_turns_push_on_after_a_successful_test_push()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();

        var result = await service.InstallAsync(InstallRequest(certificateId) with { EnablePush = true }, "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.Ok, result.Status);
        Assert.Equal(DeploymentPushResult.Ok, result.Push!.Status);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        Assert.True(target.Ssh!.PushEnabled);
        Assert.Equal("admin", target.Ssh.UserName);
        var push = Assert.Single(ssh.PushConnections);
        Assert.Equal("root", push.UserName);
        Assert.Null(push.Password);
        Assert.Contains("ecdsa-sha2-nistp256", ssh.Commands.Single(command => command.Command.StartsWith("sudo -k -S -p '' sh", StringComparison.Ordinal)).Input);
    }

    [Fact]
    public async Task Setup_keeps_push_off_when_root_logins_are_refused()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        ssh.RootLoginAllowed = false;

        var result = await service.InstallAsync(InstallRequest(certificateId) with { EnablePush = true }, "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.Ok, result.Status);
        Assert.Equal(DeploymentPushResult.Failed, result.Push!.Status);
        Assert.False(Assert.Single(await targets.ListAsync(CancellationToken.None)).Ssh!.PushEnabled);
    }

    [Fact]
    public async Task Push_refuses_a_changed_host_key_and_reports_a_busy_agent()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        await service.InstallAsync(InstallRequest(certificateId) with { EnablePush = true }, "https://ca.lab.test", CancellationToken.None);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));

        ssh.AgentExitCode = DeploymentPushService.AgentBusyExitCode;
        var busy = await _pushes!.PushAsync(target.Id, CancellationToken.None);
        ssh.PresentedHostKey = "SHA256:attacker000000000000000000000000000000000";
        var changed = await _pushes.PushAsync(target.Id, CancellationToken.None);

        Assert.Equal(DeploymentPushResult.Busy, busy!.Status);
        Assert.Equal(DeploymentPushResult.Failed, changed!.Status);
        Assert.Contains("differs", changed.Output);
        Assert.Equal(3, ssh.PushConnections.Count);
        Assert.Equal(DeploymentPushResult.Failed, (await targets.GetAsync(target.Id, CancellationToken.None))!.Ssh!.LastPush!.Status);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("mingw")]
    public async Task Windows_hosts_get_the_powershell_agent_without_push(string platform)
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        ssh.Platform = platform;

        var detected = await service.DetectAsync(new SshDetectRequest(Connection(HostKey)), CancellationToken.None);
        var result = await service.InstallAsync(InstallRequest(certificateId) with { EnablePush = true, InstallRootCa = true }, "https://ca.lab.test", CancellationToken.None);

        Assert.True(detected.Facts!.IsWindows);
        Assert.Equal(["iis", "rdp", "windows-certstore"], detected.Suggestion!.ProfileIds);
        Assert.Equal(["win01.lab.test", "pve01.lab.test"], detected.Suggestion.DnsNames);
        Assert.Equal(SshSetupResult.Ok, result.Status);
        Assert.Null(result.Push);
        Assert.Empty(ssh.PushConnections);
        var setup = ssh.Commands[^1];
        Assert.Equal(WindowsAgentScript.PowerShellCommand, setup.Command);
        Assert.StartsWith("# HomeCA guided setup", setup.Input);
        Assert.Contains("Cert:\\LocalMachine\\Root\\", setup.Input);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        Assert.NotNull(await targets.AuthenticateAsync(target.Id, WindowsTokenFrom(setup.Input!), CancellationToken.None));
        Assert.False(target.Ssh!.PushEnabled);
        Assert.DoesNotContain(WindowsTokenFrom(setup.Input!), result.Output);
    }

    [Fact]
    public async Task Windows_setup_requires_an_administrator_before_anything_changes()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        ssh.Platform = "windows";
        ssh.WindowsAdministrator = false;

        var result = await service.InstallAsync(InstallRequest(certificateId), "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.SudoFailed, result.Status);
        Assert.Empty(await targets.ListAsync(CancellationToken.None));
    }

    [Fact]
    public void Windows_agent_locks_verifies_and_passes_the_pfx_password_to_the_profile_script()
    {
        var target = new DeploymentTarget("0123456789abcdef", "IIS \"01\" Grüße $x", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var agent = WindowsAgentScript.Render("https://ca.lab.test/", target);
        var install = WindowsAgentScript.RenderInstallCommands("https://ca.lab.test", target, "secret-token");

        Assert.DoesNotContain("\r", agent);
        Assert.True(agent.All(character => character < 128), "agent.ps1 must stay ASCII");
        Assert.Contains("$HomeCaUrl = 'https://ca.lab.test'", agent);
        Assert.Contains("[IO.File]::Open(\"$StateDir.lock\", 'OpenOrCreate', 'ReadWrite', 'None')", agent);
        Assert.Contains("exit 75", agent);
        Assert.Contains("$env:HOMECA_PFX_PASSWORD = $token", agent);
        Assert.Contains("HomeCA\\Backups\\01234567", agent);
        Assert.DoesNotContain("secret-token", agent);
        Assert.Contains("secret-token", install);
        Assert.Contains("Register-ScheduledTask -TaskPath '\\HomeCA\\' -TaskName 'Agent 01234567'", install);
    }

    [Fact]
    public void Windows_setup_checks_administrator_rights_and_backs_up_before_writing()
    {
        var target = new DeploymentTarget("0123456789abcdef", "IIS", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var script = WindowsAgentScript.RenderSetup("https://ca.lab.test", target, "Tok-en_1", "# agent\n", "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", true, 5);

        var adminCheck = script.IndexOf("WindowsBuiltInRole]::Administrator", StringComparison.Ordinal);
        var backup = script.IndexOf("Backup-HomeCAFile @($TokenFile", StringComparison.Ordinal);
        var write = script.IndexOf("Set-Content -LiteralPath $TokenFile -Value 'Tok-en_1'", StringComparison.Ordinal);
        Assert.True(adminCheck > 0 && adminCheck < backup && backup < write);
        Assert.Contains("Unregister-ScheduledTask", script);
        Assert.Contains("Export-ScheduledTask", script);
        Assert.Contains("AddMinutes(5)", script);
        Assert.Throws<ArgumentException>(() => WindowsAgentScript.RenderSetup("https://ca.lab.test", target, "tok'en", "# agent", "PEM", false, 0));
        Assert.Throws<ArgumentException>(() => WindowsAgentScript.RenderSetup("https://ca.lab.test", target, "token", "x\n'@\nRemove-Item C:\\", "PEM", false, 0));
    }

    [Fact]
    public void PowerShell_progress_records_are_dropped_from_error_output()
    {
        var error = "#< CLIXML\n<Objs Version=\"1.1.0.1\"><Obj S=\"progress\" /></Objs>\nhomeca-setup: real error";

        Assert.Equal("homeca-setup: real error", SshSetupService.WithoutPowerShellProgress(error));
    }

    [Fact]
    public void Windows_bootstrap_fits_into_a_cmd_command_line()
    {
        var encoded = WindowsAgentScript.PowerShellCommand.Split(' ')[^1];
        var bootstrap = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.True(WindowsAgentScript.PowerShellCommand.Length < 8191);
        Assert.Contains("[Console]::OpenStandardInput().CopyTo($buffer)", bootstrap);
        Assert.Contains("exit $code", bootstrap);
    }

    [Fact]
    public void PowerShell_scripts_parse_without_errors()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = new DeploymentTarget("0123456789abcdef", "IIS", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);
        var seed = System.Text.Json.JsonSerializer.Deserialize<List<TargetProfile>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "profiles.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var agent = WindowsAgentScript.Render("https://ca.lab.test", target);
        var scripts = new Dictionary<string, string>
        {
            ["agent.ps1"] = agent,
            ["setup.ps1"] = WindowsAgentScript.RenderSetup("https://ca.lab.test", target, "token", agent, "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", true, 5),
            ["detect.ps1"] = WindowsAgentScript.DetectionScript,
            ["install-commands.ps1"] = WindowsAgentScript.RenderInstallCommands("https://ca.lab.test", target, "token"),
            ["bootstrap.ps1"] = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(WindowsAgentScript.PowerShellCommand.Split(' ')[^1]))
        };
        foreach (var profile in seed.Where(profile => ProfileScripts.Resolve(profile) == ProfileScripts.PowerShell))
            scripts[$"install-{profile.Id}.ps1"] = ProfileScripts.Render(profile, ProfileScripts.PowerShell);
        var directory = Path.Combine(_fixture.RootPath, "ps");
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in scripts) File.WriteAllText(Path.Combine(directory, name), content, new System.Text.UTF8Encoding(true));

        var check = "$failed = 0; foreach ($file in Get-ChildItem -LiteralPath '" + directory + "' -Filter *.ps1) { $errors = $null; [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$errors); foreach ($e in $errors) { Write-Output \"$($file.Name):$($e.Extent.StartLineNumber): $($e.Message)\"; $failed = 1 } }; exit $failed";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", check]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, output);
    }

    [Fact]
    public async Task Agent_pfx_is_protected_with_the_target_token()
    {
        var (_, _, _, certificateId) = await CreateServiceAsync();
        var storage = _fixture.CreateStorage();
        using var withKey = CertificatePfxExporter.LoadCertificateWithExportablePrivateKey(Path.Combine(_fixture.RootPath, "certificates", certificateId, "certificate.pfx"));

        var pfx = CertificatePfxExporter.ExportWithIssuingCertificate(withKey, Path.Combine(_fixture.RootPath, "exports", certificateId, "chain.pem"), "target-token");

        var loaded = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12Collection(pfx, "target-token");
        Assert.Contains(loaded, certificate => certificate.HasPrivateKey);
        Assert.Equal(2, loaded.Count);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12Collection(pfx, "wrong"));
    }

    [Fact]
    public async Task Queued_push_retries_while_another_agent_run_holds_the_lock()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        await service.InstallAsync(InstallRequest(certificateId) with { EnablePush = true }, "https://ca.lab.test", CancellationToken.None);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        ssh.BusyRuns = 2;

        await _pushes!.PushQueuedAsync(target.Id, CancellationToken.None);

        Assert.Equal(4, ssh.PushConnections.Count);
        Assert.Equal(DeploymentPushResult.Ok, (await targets.GetAsync(target.Id, CancellationToken.None))!.Ssh!.LastPush!.Status);
        Assert.Empty(_notifications!.Requests);
    }

    [Fact]
    public void Agent_releases_its_lock_on_signals()
    {
        var target = new DeploymentTarget("0123456789abcdef", "pve", "abcdef", null, 443, null, DateTimeOffset.UtcNow, null, null, null, null);

        var script = DeploymentAgentScript.Render("https://ca.lab.test", target);

        Assert.Contains("LOCK_DIR=\"$STATE_DIR.lock\"", script);
        Assert.Contains("trap 'exit 1' HUP INT TERM", script);
        Assert.Contains("exit 75", script);
    }

    [Fact]
    public async Task Setup_reports_progress_lines_for_the_wizard()
    {
        var (service, _, _, certificateId) = await CreateServiceAsync();
        var progressId = Guid.NewGuid().ToString("N");

        var result = await service.InstallAsync(InstallRequest(certificateId) with { ProgressId = progressId }, "https://ca.lab.test", CancellationToken.None);

        var progress = _progress.Get(progressId, 0);
        Assert.Equal(SshSetupResult.Ok, result.Status);
        Assert.True(progress.Done);
        Assert.Contains(progress.Lines, line => line.EndsWith("homeca: connecting to admin@pve01.lab.test:22", StringComparison.Ordinal));
        Assert.Contains(progress.Lines, line => line.EndsWith("homeca: setup script finished with exit code 0", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Lines, line => line.Contains(SshPassword, StringComparison.Ordinal));
        Assert.Empty(_progress.Get(progressId, progress.Next).Lines);
    }

    [Fact]
    public async Task Setup_timeout_returns_the_output_so_far_without_the_token()
    {
        var (service, _, ssh, certificateId) = await CreateServiceAsync();
        ssh.SetupTimesOut = true;
        var progressId = Guid.NewGuid().ToString("N");

        var result = await service.InstallAsync(InstallRequest(certificateId) with { ProgressId = progressId }, "https://ca.lab.test", CancellationToken.None);

        Assert.Equal(SshSetupResult.Failed, result.Status);
        Assert.Contains("did not finish", result.Message);
        Assert.Contains("homeca-setup: backup done", result.Output);
        Assert.Contains("token *** written", result.Output);
        Assert.Contains(_progress.Get(progressId, 0).Lines, line => line.Contains("did not finish", StringComparison.Ordinal));
    }

    [Fact]
    public void Progress_ignores_ids_that_are_not_32_hex_digits()
    {
        var progress = new SshSetupProgress();

        progress.Append("../../etc", "line");
        progress.Append("0123456789abcdef0123456789abcdef", "line");

        Assert.False(SshSetupProgress.IsValidId("../../etc"));
        Assert.Single(progress.Get("0123456789abcdef0123456789abcdef", 0).Lines);
    }

    [Fact]
    public async Task Failed_queued_push_sends_a_notification_and_disabled_push_is_skipped()
    {
        var (service, targets, ssh, certificateId) = await CreateServiceAsync();
        await service.InstallAsync(InstallRequest(certificateId) with { EnablePush = true }, "https://ca.lab.test", CancellationToken.None);
        var target = Assert.Single(await targets.ListAsync(CancellationToken.None));
        ssh.AgentExitCode = 1;

        await _pushes!.PushQueuedAsync(target.Id, CancellationToken.None);
        await targets.DisablePushAsync(target.Id, CancellationToken.None);
        await _pushes.PushQueuedAsync(target.Id, CancellationToken.None);

        var notification = Assert.Single(_notifications!.Requests);
        Assert.Contains("push", notification.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, ssh.PushConnections.Count);
    }

    public void Dispose() => _fixture.Dispose();

    /// <summary>Plays a Debian host with sudo: answers the detection script, the sudo check and the setup script.</summary>
    private sealed class FakeSshClientFactory : ISshClientFactory
    {
        public string PresentedHostKey { get; set; } = HostKey;
        public bool SudoAccepts { get; set; } = true;
        public bool RootLoginAllowed { get; set; } = true;
        public int AgentExitCode { get; set; }
        /// <summary>Number of pushes that find another agent run in progress before AgentExitCode applies.</summary>
        public int BusyRuns { get; set; }
        /// <summary>linux, windows (cmd as default shell) or mingw (Windows with Git for Windows in the PATH).</summary>
        public string Platform { get; set; } = "linux";
        public bool WindowsAdministrator { get; set; } = true;
        /// <summary>The setup script prints a line with the token and then hangs until the timeout.</summary>
        public bool SetupTimesOut { get; set; }
        public List<(string Command, string? Input)> Commands { get; } = [];
        public List<SshCredentials> PushConnections { get; } = [];

        public Task<ISshSession> ConnectAsync(SshCredentials credentials, string? expectedHostKey, CancellationToken ct)
        {
            var presented = new SshHostKey("ssh-ed25519", PresentedHostKey);
            var push = credentials.UserName == "root" && credentials.PrivateKey is not null;
            if (push) PushConnections.Add(credentials);
            if (expectedHostKey != PresentedHostKey) throw new SshHostKeyNotTrustedException(presented, expectedHostKey);
            if (push && !RootLoginAllowed) throw new SshSetupConnectionException(SshSetupResult.AuthenticationFailed, "The server rejected the user name or the credentials.");
            return Task.FromResult<ISshSession>(new Session(this, presented, push));
        }

        private sealed class Session(FakeSshClientFactory owner, SshHostKey hostKey, bool push) : ISshSession
        {
            public SshHostKey HostKey { get; } = hostKey;

            public Task<SshCommandResult> RunAsync(string command, string? input, TimeSpan timeout, CancellationToken ct)
            {
                // The forced command on a real device runs the agent whatever HomeCA sends.
                if (push && owner.BusyRuns > 0) { owner.BusyRuns--; return Task.FromResult(new SshCommandResult(DeploymentPushService.AgentBusyExitCode, "", "homeca-agent: another run is in progress")); }
                if (push) return Task.FromResult(new SshCommandResult(owner.AgentExitCode, "homeca-agent: certificate abc is already installed", ""));
                if (owner.SetupTimesOut && input?.Contains("STATE_DIR=", StringComparison.Ordinal) == true)
                    throw new SshSetupConnectionException(SshSetupResult.Failed, "The command did not finish within 10 minutes; the output so far is below.",
                        output: $"homeca-setup: backup done\nhomeca-setup: token {TokenFrom(input)} written\n");
                owner.Commands.Add((command, input));
                if (owner.Platform != "linux")
                {
                    if (command == "sh -s") return Task.FromResult(owner.Platform == "mingw"
                        ? new SshCommandResult(0, "kernel=MINGW64_NT-10.0-26100\nhostname=WIN01\n", "")
                        : new SshCommandResult(1, "", "'sh' is not recognized as an internal or external command"));
                    if (command != WindowsAgentScript.PowerShellCommand) return Task.FromResult(new SshCommandResult(127, "", "unexpected command"));
                    if (input == WindowsAgentScript.DetectionScript)
                        return Task.FromResult(new SshCommandResult(0, $"platform=windows\nuser=WIN01\\admin\nuid={(owner.WindowsAdministrator ? 0 : 1000)}\nsudo={(owner.WindowsAdministrator ? "root" : "none")}\nhostname=WIN01\nfqdn=win01.lab.test\nos=Microsoft Windows Server 2022 Standard\nkernel=Windows\naddress=192.168.10.20\ntool=powershell\nscheduler=taskscheduler\ntruststore=windows\nservice=iis\nservice=rdp\n", ""));
                    return Task.FromResult(new SshCommandResult(0, "homeca-setup: done " + WindowsTokenFrom(input!), ""));
                }
                var result = command switch
                {
                    "sh -s" => new SshCommandResult(0, ProxmoxDetection, ""),
                    "sudo -k -S -p '' true" => new SshCommandResult(owner.SudoAccepts && input == SshPassword + "\n" ? 0 : 1, "", ""),
                    // A careless script could echo secrets; the service must remove them from the output.
                    _ => new SshCommandResult(0, "homeca-setup: done " + TokenFrom(input!) + " " + SshPassword, "")
                };
                return Task.FromResult(result);
            }

            public void Dispose() { }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "HomeCA.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
