using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeCA.Service.Automation;

/// <summary>Sends renewal outcomes through SMTP or Microsoft Graph without logging credentials or message recipients.</summary>
public sealed class RenewalMailNotificationService(
    RenewalNotificationSettingsRegistry settings,
    IHttpClientFactory httpClientFactory,
    ILogger<RenewalMailNotificationService> logger)
{
    public async Task SendRenewedAsync(string subject, DateTime expiresAt, CancellationToken ct) =>
        await SendAsync("HomeCA: Zertifikat automatisch erneuert", $"Das Zertifikat '{subject}' wurde automatisch erneuert. Es ist nun bis {expiresAt:dd.MM.yyyy} gültig.", ct);

    public async Task SendFailureAsync(string subject, Exception exception, CancellationToken ct) =>
        await SendAsync("HomeCA: automatische Zertifikatserneuerung fehlgeschlagen", $"Die automatische Erneuerung für '{subject}' ist fehlgeschlagen. Prüfen Sie die HomeCA-Protokolle. Fehler: {exception.Message}", ct);

    public async Task SendTestAsync(CancellationToken ct) =>
        await SendAsync("HomeCA: E-Mail-Versand getestet", "Der Versand von Erneuerungsbenachrichtigungen ist korrekt eingerichtet.", ct);

    private async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var configuration = await settings.GetStoredAsync(ct);
        if (!configuration.Enabled) return;

        try
        {
            if (configuration.Provider == "m365") await SendM365Async(configuration, subject, body, ct);
            else await SendSmtpAsync(configuration, subject, body, ct);
            logger.LogInformation("Sent renewal notification via {Provider}", configuration.Provider);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not send renewal notification via {Provider}", configuration.Provider);
            throw;
        }
    }

    private static async Task SendSmtpAsync(StoredRenewalNotificationSettings configuration, string subject, string body, CancellationToken ct)
    {
        using var message = new MailMessage { From = new MailAddress(configuration.FromAddress), Subject = subject, Body = body, IsBodyHtml = false };
        foreach (var recipient in configuration.Recipients) message.To.Add(recipient);
        using var client = new SmtpClient(configuration.SmtpHost, configuration.SmtpPort)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrWhiteSpace(configuration.SmtpUserName) ? null : new NetworkCredential(configuration.SmtpUserName, configuration.SmtpPassword)
        };
        await client.SendMailAsync(message, ct);
    }

    private async Task SendM365Async(StoredRenewalNotificationSettings configuration, string subject, string body, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(RenewalMailNotificationService));
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{Uri.EscapeDataString(configuration.M365TenantId)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = configuration.M365ClientId,
                ["client_secret"] = configuration.M365ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            })
        };
        using var tokenResponse = await client.SendAsync(tokenRequest, ct);
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<MicrosoftTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Microsoft 365 did not return an access token.");
        if (string.IsNullOrWhiteSpace(token.AccessToken)) throw new InvalidOperationException("Microsoft 365 did not return an access token.");

        var payload = new
        {
            message = new { subject, body = new { contentType = "Text", content = body }, toRecipients = configuration.Recipients.Select(address => new { emailAddress = new { address } }) },
            saveToSentItems = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(configuration.M365SenderMailbox)}/sendMail")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record MicrosoftTokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
}
