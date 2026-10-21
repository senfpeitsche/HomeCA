using HomeCA.Service.Acme;
using HomeCA.Service.Automation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Notifications;
using HomeCA.Service.Operations;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Resources;
using HomeCA.Service.Revocation;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeCA.Tests;

public sealed class OperationsAutomationTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Scheduled_backup_keeps_the_newest_and_copies_to_the_second_directory()
    {
        var offsite = Path.Combine(Path.GetTempPath(), $"homeca-offsite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(offsite);
        try
        {
            var backups = CreateBackupService(new RecordingChannel());
            await backups.SaveAsync(new SaveBackupScheduleRequest(true, 24, 2, offsite), CancellationToken.None);

            for (var run = 0; run < 3; run++) Assert.Null((await backups.RunAsync(CancellationToken.None)).LastError);

            Assert.Equal(2, Directory.GetFiles(_fixture.BackupPath, "homeca-*.hcab").Length);
            Assert.Equal(2, Directory.GetFiles(offsite, "homeca-*.hcab").Length);
            var schedule = await backups.GetAsync(CancellationToken.None);
            Assert.NotNull(schedule.LastSuccessAt);
            Assert.False(await backups.IsDueAsync(DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(await backups.IsDueAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None));
        }
        finally { Directory.Delete(offsite, true); }
    }

    [Fact]
    public async Task Missing_second_directory_is_recorded_and_notified()
    {
        var channel = new RecordingChannel();
        var backups = CreateBackupService(channel);
        var missing = Path.Combine(Path.GetTempPath(), $"homeca-unmounted-{Guid.NewGuid():N}");
        await backups.SaveAsync(new SaveBackupScheduleRequest(true, 24, 5, missing), CancellationToken.None);

        var result = await backups.RunAsync(CancellationToken.None);

        Assert.Contains("does not exist", result.LastError);
        Assert.Contains(channel.Messages, message => message.Contains("does not exist"));
    }

    [Theory]
    [InlineData(0, 14, null)]
    [InlineData(24, 0, null)]
    [InlineData(24, 14, "relative/path")]
    public async Task Invalid_schedules_are_rejected(int interval, int retention, string? offsite) =>
        await Assert.ThrowsAsync<ArgumentException>(() => CreateBackupService(new RecordingChannel()).SaveAsync(new SaveBackupScheduleRequest(true, interval, retention, offsite), CancellationToken.None));

    [Fact]
    public async Task Second_directory_must_not_be_inside_the_data_directory() =>
        await Assert.ThrowsAsync<ArgumentException>(() => CreateBackupService(new RecordingChannel())
            .SaveAsync(new SaveBackupScheduleRequest(true, 24, 14, Path.Combine(_fixture.RootPath, "state")), CancellationToken.None));

    [Fact]
    public async Task Attention_lists_unplanned_expiry_failed_renewals_outdated_devices_and_missing_backups()
    {
        var (certificates, plans, targets, attention) = await SetupAttentionAsync();
        var unplanned = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["soon.lab.test"], [], 10), CancellationToken.None);
        var planned = await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["planned.lab.test"], [], 10), CancellationToken.None);
        var plan = await plans.AddAsync(new CreateRenewalPlanRequest(planned.Id, 5), CancellationToken.None);
        await plans.RecordFailureAsync(plan.Id, "issuing CA expired", CancellationToken.None);
        var target = (await targets.CreateAsync(new SaveDeploymentTargetRequest("pve01", planned.Id, "pve01.lab.test"), CancellationToken.None)).Target;
        await targets.RecordProbeAsync(target.Id, new DeploymentProbeResult(DateTimeOffset.UtcNow, DeploymentProbeResult.Outdated, "ab"), CancellationToken.None);

        var items = await attention.GetAsync(CancellationToken.None);

        Assert.Contains(items, item => item.Kind == "certificate-expiring" && item.ReferenceId == unplanned.Id);
        Assert.DoesNotContain(items, item => item.Kind == "certificate-expiring" && item.ReferenceId == planned.Id);
        Assert.Contains(items, item => item.Kind == "renewal-failed" && item.Detail == "issuing CA expired");
        Assert.Contains(items, item => item.Kind == "deployment-outdated" && item.Subject == "pve01");
        Assert.Contains(items, item => item.Kind == "backup-missing");
        Assert.Equal(AttentionItem.Error, items[0].Severity);
    }

    [Fact]
    public async Task A_renewed_predecessor_does_not_count_as_expiring()
    {
        var (certificates, _, _, attention) = await SetupAttentionAsync();
        await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["nas.lab.test"], [], 10), CancellationToken.None);
        await certificates.IssueAsync(new IssueCertificateRequest("TLS", ["nas.lab.test"], [], 365), CancellationToken.None);

        var items = await attention.GetAsync(CancellationToken.None);

        Assert.DoesNotContain(items, item => item.Kind == "certificate-expiring");
    }

    private BackupScheduleService CreateBackupService(RecordingChannel channel) =>
        new(_fixture.CreateStorage(), CreateNotifications(channel), NullLogger<BackupScheduleService>.Instance);

    private NotificationService CreateNotifications(RecordingChannel channel)
    {
        var storage = _fixture.CreateStorage();
        var registry = new NotificationChannelRegistry(storage);
        registry.SaveAsync(new SaveNotificationSettingsRequest("en", [new(null, "webhook", "Recorder", "https://recorder.lab.test/hook")]), CancellationToken.None).GetAwaiter().GetResult();
        var factory = new DeploymentTargetTests.SingleClientFactory(channel);
        var mail = new RenewalMailNotificationService(new RenewalNotificationSettingsRegistry(storage), factory, NullLogger<RenewalMailNotificationService>.Instance);
        var localizer = new StringLocalizer<UiText>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        return new NotificationService(registry, mail, factory, localizer, NullLogger<NotificationService>.Instance);
    }

    private async Task<(CertificateIssuanceService, RenewalPlanRegistry, DeploymentTargetRegistry, AttentionService)> SetupAttentionAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        var plans = new RenewalPlanRegistry(storage, NullLogger<RenewalPlanRegistry>.Instance);
        var targets = new DeploymentTargetRegistry(storage, NullLogger<DeploymentTargetRegistry>.Instance);
        var attention = new AttentionService(storage, certificates, authorities, revocations, plans, targets, CreateBackupService(new RecordingChannel()));
        return (certificates, plans, targets, attention);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class RecordingChannel : HttpMessageHandler
    {
        public List<string> Messages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Messages.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
