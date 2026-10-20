using HomeCA.Service.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class RenewalBackgroundServiceTests
{
    [Fact]
    public void Failure_Notification_Is_Sent_At_Most_Once_Per_Interval_And_Plan()
    {
        var service = new RenewalBackgroundService(new ServiceCollection().BuildServiceProvider(), NullLogger<RenewalBackgroundService>.Instance);
        var start = DateTimeOffset.UtcNow;

        Assert.True(service.ShouldSendFailureNotification("plan-a", start));
        Assert.False(service.ShouldSendFailureNotification("plan-a", start.AddHours(1)));
        Assert.False(service.ShouldSendFailureNotification("plan-a", start.AddHours(23)));
        Assert.True(service.ShouldSendFailureNotification("plan-b", start.AddHours(1)));
        Assert.True(service.ShouldSendFailureNotification("plan-a", start + RenewalBackgroundService.FailureNotificationInterval));
    }
}
