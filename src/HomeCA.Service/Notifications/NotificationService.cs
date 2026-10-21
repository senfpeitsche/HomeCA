using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HomeCA.Service.Automation;
using HomeCA.Service.Resources;
using Microsoft.Extensions.Localization;

namespace HomeCA.Service.Notifications;

/// <summary>
/// Delivers an event to e-mail and to every enabled push or webhook channel that subscribed to it, in the
/// configured notification language. A failing channel is logged and does not stop the others.
/// </summary>
public sealed class NotificationService(
    NotificationChannelRegistry channels,
    RenewalMailNotificationService mail,
    IHttpClientFactory httpClientFactory,
    IStringLocalizer<UiText> text,
    ILogger<NotificationService> logger)
{
    public async Task SendAsync(NotificationEvent notificationEvent, string subject, string detail, CancellationToken ct)
    {
        var settings = await channels.GetStoredAsync(ct);
        var (title, message) = Compose(notificationEvent, subject, detail, settings.Language);

        try
        {
            await mail.SendMessageAsync(title, message, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The mail service already logged the delivery error with its stack trace.
            logger.LogWarning("E-mail notification {Event} was not delivered: {Reason}", notificationEvent, exception.Message);
        }

        foreach (var channel in settings.Channels.Where(channel => channel.Enabled && channel.Receives(notificationEvent)))
        {
            try { await DeliverAsync(channel, notificationEvent, title, message, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Notification {Event} was not delivered to channel {Channel}: {Reason}", notificationEvent, channel.Name, exception.Message);
            }
        }
    }

    /// <summary>Sends a test message to one channel and lets the error surface to the caller.</summary>
    public async Task TestChannelAsync(string channelId, CancellationToken ct)
    {
        var settings = await channels.GetStoredAsync(ct);
        var channel = settings.Channels.FirstOrDefault(item => item.Id == channelId)
            ?? throw new KeyNotFoundException("The notification channel does not exist.");
        var (title, message) = Compose(NotificationEvent.Test, string.Empty, string.Empty, settings.Language);
        await DeliverAsync(channel, NotificationEvent.Test, title, message, ct);
    }

    internal (string Title, string Message) Compose(NotificationEvent notificationEvent, string subject, string detail, string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            return (text[$"Notification{notificationEvent}Title"], text[$"Notification{notificationEvent}Message", subject, detail]);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private async Task DeliverAsync(StoredNotificationChannel channel, NotificationEvent notificationEvent, string title, string message, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(NotificationService));
        client.Timeout = TimeSpan.FromSeconds(15);
        using var request = channel.Type switch
        {
            NotificationChannelRegistry.Ntfy => NtfyRequest(channel, notificationEvent, title, message),
            NotificationChannelRegistry.Gotify => GotifyRequest(channel, notificationEvent, title, message),
            _ => new HttpRequestMessage(HttpMethod.Post, channel.Url)
            {
                // "text" serves Slack and Mattermost, "content" Discord; other receivers read event, title and message.
                Content = JsonContent.Create(new { @event = notificationEvent.ToString(), title, message, text = $"{title}\n{message}", content = $"**{title}**\n{message}" })
            }
        };
        if (channel.Type == NotificationChannelRegistry.Webhook && !string.IsNullOrWhiteSpace(channel.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channel.Token);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage NtfyRequest(StoredNotificationChannel channel, NotificationEvent notificationEvent, string title, string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{channel.Url}/{channel.Topic}") { Content = new StringContent(message) };
        // ntfy reads non-ASCII header values only when they are RFC 2047 encoded.
        request.Headers.TryAddWithoutValidation("Title", $"=?UTF-8?B?{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(title))}?=");
        request.Headers.TryAddWithoutValidation("Tags", IsProblem(notificationEvent) ? "warning,lock" : "lock");
        request.Headers.TryAddWithoutValidation("Priority", IsProblem(notificationEvent) ? "high" : "default");
        if (!string.IsNullOrWhiteSpace(channel.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channel.Token);
        return request;
    }

    private static HttpRequestMessage GotifyRequest(StoredNotificationChannel channel, NotificationEvent notificationEvent, string title, string message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{channel.Url}/message")
        {
            Content = JsonContent.Create(new { title, message, priority = IsProblem(notificationEvent) ? 8 : 4 })
        };
        request.Headers.TryAddWithoutValidation("X-Gotify-Key", channel.Token);
        return request;
    }

    private static bool IsProblem(NotificationEvent notificationEvent) =>
        notificationEvent is NotificationEvent.RenewalFailed or NotificationEvent.DeploymentOutdated or NotificationEvent.BackupFailed or NotificationEvent.DeploymentPushFailed;
}
