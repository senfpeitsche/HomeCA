using HomeCA.Service.Automation;
namespace HomeCA.Tests;

public sealed class RenewalNotificationSettingsTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Update_Persists_Secrets_But_Never_Returns_Them()
    {
        var registry = new RenewalNotificationSettingsRegistry(_fixture.CreateStorage());
        var saved = await registry.UpdateAsync(new UpdateRenewalNotificationSettingsRequest(
            true, "smtp", ["admin@example.test"], "homeca@example.test", "smtp.example.test", 587, "homeca", "secret-password"), CancellationToken.None);

        Assert.True(saved.Enabled);
        Assert.True(saved.HasSmtpPassword);
        Assert.Equal("smtp.example.test", saved.SmtpHost);
        Assert.DoesNotContain("secret-password", System.Text.Json.JsonSerializer.Serialize(saved));

        var loaded = await registry.GetAsync(CancellationToken.None);
        Assert.True(loaded.HasSmtpPassword);
        Assert.DoesNotContain("secret-password", System.Text.Json.JsonSerializer.Serialize(loaded));
    }

    [Fact]
    public async Task Enabled_Settings_Require_A_Recipient()
    {
        var registry = new RenewalNotificationSettingsRegistry(_fixture.CreateStorage());

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpdateAsync(new UpdateRenewalNotificationSettingsRequest(
            true, "smtp", [], "homeca@example.test", "smtp.example.test"), CancellationToken.None));
    }

    public void Dispose() => _fixture.Dispose();
}
