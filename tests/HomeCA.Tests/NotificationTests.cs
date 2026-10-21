using System.Net;
using System.Text.Json;
using HomeCA.Service.Automation;
using HomeCA.Service.Notifications;
using HomeCA.Service.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeCA.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Tokens_are_write_only_and_kept_when_saved_empty()
    {
        var registry = new NotificationChannelRegistry(_fixture.CreateStorage());
        var saved = await registry.SaveAsync(new SaveNotificationSettingsRequest("de", [new(null, "gotify", "Gotify", "https://gotify.lab.test/", Token: "secret")]), CancellationToken.None);
        var channel = Assert.Single(saved.Channels);

        var resaved = await registry.SaveAsync(new SaveNotificationSettingsRequest("de", [new(channel.Id, "gotify", "Gotify", channel.Url)]), CancellationToken.None);

        Assert.True(Assert.Single(resaved.Channels).HasToken);
        Assert.Equal("https://gotify.lab.test", channel.Url);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(await registry.GetAsync(CancellationToken.None)));
        Assert.Equal("de", resaved.Language);
    }

    [Theory]
    [InlineData("pushover", "https://x.test", null, null)]
    [InlineData("ntfy", "https://ntfy.test", null, null)]
    [InlineData("ntfy", "https://ntfy.test", "bad topic", null)]
    [InlineData("gotify", "https://gotify.test", null, null)]
    [InlineData("webhook", "file:///etc/passwd", null, null)]
    public async Task Invalid_channels_are_rejected(string type, string url, string? topic, string? token) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new NotificationChannelRegistry(_fixture.CreateStorage())
            .SaveAsync(new SaveNotificationSettingsRequest("en", [new(null, type, "Channel", url, topic, token)]), CancellationToken.None));

    [Fact]
    public async Task Each_channel_type_gets_its_own_request_format()
    {
        var registry = new NotificationChannelRegistry(_fixture.CreateStorage());
        await registry.SaveAsync(new SaveNotificationSettingsRequest("en",
        [
            new(null, "ntfy", "Phone", "https://ntfy.lab.test", "homeca", "tk_ntfy"),
            new(null, "gotify", "Gotify", "https://gotify.lab.test", Token: "gotify-token"),
            new(null, "webhook", "Chat", "https://chat.lab.test/hook"),
            new(null, "webhook", "Only backups", "https://backup.lab.test/hook", Events: ["BackupFailed"]),
            new(null, "webhook", "Disabled", "https://off.lab.test/hook", Enabled: false)
        ]), CancellationToken.None);
        var handler = new RecordingHandler();

        await CreateService(registry, handler).SendAsync(NotificationEvent.RenewalFailed, "CN=nas.lab.test", "boom", CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        var ntfy = handler.Requests.Single(request => request.Uri == "https://ntfy.lab.test/homeca");
        Assert.Equal("high", ntfy.Headers["Priority"]);
        Assert.Equal("Bearer tk_ntfy", ntfy.Headers["Authorization"]);
        Assert.Contains("CN=nas.lab.test", ntfy.Body);
        var gotify = handler.Requests.Single(request => request.Uri == "https://gotify.lab.test/message");
        Assert.Equal("gotify-token", gotify.Headers["X-Gotify-Key"]);
        Assert.Equal(8, JsonDocument.Parse(gotify.Body).RootElement.GetProperty("priority").GetInt32());
        var webhook = JsonDocument.Parse(handler.Requests.Single(request => request.Uri == "https://chat.lab.test/hook").Body).RootElement;
        Assert.Equal("RenewalFailed", webhook.GetProperty("event").GetString());
        Assert.Contains("boom", webhook.GetProperty("text").GetString());
        Assert.Contains("boom", webhook.GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_failing_channel_does_not_stop_the_others()
    {
        var registry = new NotificationChannelRegistry(_fixture.CreateStorage());
        await registry.SaveAsync(new SaveNotificationSettingsRequest("en",
        [
            new(null, "webhook", "Broken", "https://broken.lab.test/hook"),
            new(null, "webhook", "Working", "https://working.lab.test/hook")
        ]), CancellationToken.None);
        var handler = new RecordingHandler { FailingHost = "broken.lab.test" };

        await CreateService(registry, handler).SendAsync(NotificationEvent.CertificateRenewed, "CN=a", "2027-01-01", CancellationToken.None);

        Assert.Contains(handler.Requests, request => request.Uri == "https://working.lab.test/hook");
    }

    [Fact]
    public async Task Messages_follow_the_notification_language()
    {
        var registry = new NotificationChannelRegistry(_fixture.CreateStorage());
        var service = CreateService(registry, new RecordingHandler());

        var german = service.Compose(NotificationEvent.CertificateRenewed, "CN=a", "2027-01-01", "de");
        var english = service.Compose(NotificationEvent.CertificateRenewed, "CN=a", "2027-01-01", "en");

        Assert.Equal("HomeCA: Zertifikat erneuert", german.Title);
        Assert.Equal("HomeCA: certificate renewed", english.Title);
        Assert.Contains("2027-01-01", english.Message);
    }

    private NotificationService CreateService(NotificationChannelRegistry registry, RecordingHandler handler)
    {
        var storage = _fixture.CreateStorage();
        var factory = new DeploymentTargetTests.SingleClientFactory(handler);
        var mail = new RenewalMailNotificationService(new RenewalNotificationSettingsRegistry(storage), factory, NullLogger<RenewalMailNotificationService>.Instance);
        var localizerFactory = new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        return new NotificationService(registry, mail, factory, new StringLocalizer<UiText>(localizerFactory), NullLogger<NotificationService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? FailingHost { get; init; }
        public List<(string Uri, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == FailingHost) throw new HttpRequestException("connection refused");
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value));
            Requests.Add((request.RequestUri.ToString(), headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
