using HomeCA.Service.Acme;
using HomeCA.Service.Deployments;
using HomeCA.Service.Localization;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class TargetProfileTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    // Proxmox profile exactly as shipped up to 0.4.x, before script types and localizations existed.
    private const string LegacyProxmoxProfile = """
        { "id": "proxmox", "version": "1.1", "displayName": "Proxmox VE", "purpose": "Proxmox-Weboberfläche", "keyAlgorithm": "ECC", "exportFormats": ["PEM"], "validation": { "requiresDnsName": true, "allowsIpAddress": false, "maximumValidityDays": 730 }, "documentation": "Kopiere certificate.pem nach /etc/pve/local/pveproxy-ssl.pem und key.pem nach /etc/pve/local/pveproxy-ssl.key, dann pveproxy neu starten.", "renewalScriptTemplate": "cp certificate.pem /etc/pve/local/pveproxy-ssl.pem\ncp key.pem /etc/pve/local/pveproxy-ssl.key\nsystemctl restart pveproxy" }
        """;

    private const string CustomProfile = """
        { "id": "custom", "version": "1.0", "displayName": "Mein Gerät", "purpose": "Eigenes Profil", "keyAlgorithm": "ECC", "exportFormats": ["PEM"], "validation": { "requiresDnsName": true, "allowsIpAddress": true, "maximumValidityDays": 365 }, "documentation": "Eigene Anleitung", "renewalScriptTemplate": "Restart-Service MyService" }
        """;

    [Fact]
    public async Task Every_built_in_profile_has_a_script_type_and_translations_for_every_locale()
    {
        var profiles = await new TargetProfileRegistry(_fixture.CreateStorage()).ListAsync(CancellationToken.None);
        var translatedLocales = LocaleCatalog.Locales.Select(locale => locale.Culture).Where(culture => culture != LocaleCatalog.DefaultCulture).ToList();

        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
        {
            Assert.Contains(profile.ScriptType!, ProfileScripts.Types);
            Assert.NotNull(profile.Localizations);
            foreach (var culture in translatedLocales)
            {
                Assert.True(profile.Localizations!.TryGetValue(culture, out var text), $"{profile.Id} lacks '{culture}'");
                Assert.False(string.IsNullOrWhiteSpace(text!.DisplayName), $"{profile.Id}/{culture} display name");
                Assert.False(string.IsNullOrWhiteSpace(text.Purpose), $"{profile.Id}/{culture} purpose");
                Assert.False(string.IsNullOrWhiteSpace(text.Documentation), $"{profile.Id}/{culture} documentation");
            }
        }
    }

    [Fact]
    public async Task Unedited_legacy_seed_profiles_are_migrated_and_custom_profiles_are_kept()
    {
        var path = Path.Combine(_fixture.RootPath, "profiles", "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"[{LegacyProxmoxProfile},{CustomProfile}]");

        var profiles = await new TargetProfileRegistry(_fixture.CreateStorage()).ListAsync(CancellationToken.None);

        var proxmox = Assert.Single(profiles, profile => profile.Id == "proxmox");
        Assert.Equal("Proxmox web interface", proxmox.Purpose);
        Assert.Equal("Proxmox-Weboberfläche", proxmox.Localizations!["de"].Purpose);
        Assert.Equal(ProfileScripts.Shell, proxmox.ScriptType);

        var custom = Assert.Single(profiles, profile => profile.Id == "custom");
        Assert.Equal("Eigene Anleitung", custom.Documentation);
        Assert.Null(custom.ScriptType);
        Assert.Null(custom.Localizations);
        Assert.Contains("Proxmox web interface", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task New_seed_profiles_are_added_once_and_deleted_ones_stay_deleted()
    {
        var path = Path.Combine(_fixture.RootPath, "profiles", "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // An older installation where the administrator deleted every built-in profile except Proxmox.
        await File.WriteAllTextAsync(path, $"[{LegacyProxmoxProfile},{CustomProfile}]");

        var first = await new TargetProfileRegistry(_fixture.CreateStorage()).ListAsync(CancellationToken.None);

        Assert.Contains(first, profile => profile.Id == "fritzbox");
        Assert.Contains(first, profile => profile.Id == "jellyfin");
        Assert.DoesNotContain(first, profile => profile.Id == "nginx");

        var registry = new TargetProfileRegistry(_fixture.CreateStorage());
        Assert.True(await registry.DeleteAsync("fritzbox", CancellationToken.None));
        var afterRestart = await new TargetProfileRegistry(_fixture.CreateStorage()).ListAsync(CancellationToken.None);

        Assert.DoesNotContain(afterRestart, profile => profile.Id == "fritzbox");
        Assert.Contains(afterRestart, profile => profile.Id == "custom");
    }

    [Fact]
    public async Task Edited_legacy_profile_is_not_replaced()
    {
        var path = Path.Combine(_fixture.RootPath, "profiles", "profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"[{LegacyProxmoxProfile.Replace("systemctl restart pveproxy", "systemctl reload pveproxy")}]");

        var proxmox = Assert.Single(await new TargetProfileRegistry(_fixture.CreateStorage()).ListAsync(CancellationToken.None), profile => profile.Id == "proxmox");

        Assert.Equal("Proxmox-Weboberfläche", proxmox.Purpose);
        Assert.Null(proxmox.Localizations);
    }

    [Fact]
    public async Task Editing_a_base_text_drops_only_that_translation()
    {
        var registry = new TargetProfileRegistry(_fixture.CreateStorage());
        var nginx = (await registry.ListAsync(CancellationToken.None)).Single(profile => profile.Id == "nginx");

        var updated = await registry.UpdateAsync("nginx", new UpdateTargetProfileRequest(
            nginx.Version, nginx.DisplayName, nginx.Purpose, nginx.KeyAlgorithm, nginx.ExportFormats,
            new ProfileValidationRequest(nginx.Validation.RequiresDnsName, nginx.Validation.AllowsIpAddress, nginx.Validation.MaximumValidityDays),
            "Copy the files to /srv/nginx/ssl.", nginx.RenewalScriptTemplate, nginx.ScriptType), CancellationToken.None);

        var german = updated!.Localizations!["de"];
        Assert.Null(german.Documentation);
        Assert.Equal(nginx.Localizations!["de"].Purpose, german.Purpose);
        Assert.Equal(ProfileScripts.Shell, updated.ScriptType);
    }

    [Fact]
    public async Task Unknown_script_type_is_rejected()
    {
        var registry = new TargetProfileRegistry(_fixture.CreateStorage());

        await Assert.ThrowsAsync<ArgumentException>(() => registry.AddAsync(new CreateTargetProfileRequest(
            "bat-profile", "1.0", "Batch", "Test", "ECC", ["PEM"], new ProfileValidationRequest(true, true, 365), "Docs", "echo", "bat"), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "Import-PfxCertificate -FilePath certificate.pfx", "ps1")]
    [InlineData(null, "cp key.pem /etc/ssl/key.pem", "sh")]
    [InlineData("cli", "conf t", "cli")]
    [InlineData("unknown", "Restart-Service x", "ps1")]
    public void Script_type_falls_back_to_detection(string? explicitType, string template, string expected) =>
        Assert.Equal(expected, ProfileScripts.Resolve(explicitType, template));

    [Theory]
    [InlineData("proxmox", "install.sh", "#!/bin/sh")]
    [InlineData("iis", "install.ps1", "$ErrorActionPreference = 'Stop'")]
    [InlineData("rdp", "install.ps1", "SSLCertificateSHA1Hash = $certificate.Thumbprint")]
    [InlineData("cisco-switch", "device-commands.txt", "not a shell script")]
    public async Task Deployment_package_uses_the_profile_script_type(string profileId, string scriptFile, string expectedContent)
    {
        var certificates = await CreateIssuanceAsync();

        var result = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["device.example.com"], [], 90, profileId == "proxmox" ? "ECC" : "RSA", 2048, profileId), CancellationToken.None);

        var scriptPath = Path.Combine(result.ExportPath, scriptFile);
        Assert.True(File.Exists(scriptPath), $"{scriptFile} missing");
        Assert.Contains(expectedContent, await File.ReadAllTextAsync(scriptPath));
        Assert.DoesNotContain("\r\n", await File.ReadAllTextAsync(scriptPath));
        foreach (var other in new[] { "install.sh", "install.ps1", "device-commands.txt" }.Where(name => name != scriptFile))
            Assert.False(File.Exists(Path.Combine(result.ExportPath, other)), $"unexpected {other}");
        Assert.Contains(scriptFile, await File.ReadAllTextAsync(Path.Combine(result.ExportPath, "README.md")));
        Assert.Contains(scriptFile, await File.ReadAllTextAsync(Path.Combine(result.ExportPath, "checksums.json")));
    }

    private async Task<CertificateIssuanceService> CreateIssuanceAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        return new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();
}
