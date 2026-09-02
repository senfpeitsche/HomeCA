using HomeCA.Service.Infrastructure;
using HomeCA.Service.Security;
using HomeCA.Service.Pki;
using HomeCA.Service.Domains;
using HomeCA.Service.Profiles;
using HomeCA.Service.Connectors;
using HomeCA.Service.Acme;
using HomeCA.Service.Operations;
using HomeCA.Service.Revocation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Automation;
using Microsoft.Extensions.Options;

namespace HomeCA.Service.Endpoints;
 static class RenewalEndpoints
{
    public static void MapRenewalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapGet("/renewal-plans", async (RenewalPlanRegistry plans, CancellationToken ct) => Results.Ok(await plans.ListAsync(ct)));
        api.MapPost("/renewal-plans", async (CreateRenewalPlanRequest body, RenewalPlanRegistry plans, CancellationToken ct) => Results.Ok(await plans.AddAsync(body, ct)));
        api.MapPut("/renewal-plans/{id}", async (string id, UpdateRenewalPlanRequest body, RenewalPlanRegistry plans, CancellationToken ct) =>
        {
            var plan = await plans.UpdateAsync(id, body, ct);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });
        api.MapDelete("/renewal-plans/{id}", async (string id, RenewalPlanRegistry plans, CancellationToken ct) => await plans.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        
        // Renewal notification delivery settings. The GET response deliberately omits passwords and client secrets.
        api.MapGet("/renewal-notifications", async (RenewalNotificationSettingsRegistry settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        api.MapPut("/renewal-notifications", async (UpdateRenewalNotificationSettingsRequest body, RenewalNotificationSettingsRegistry settings, CancellationToken ct) =>
        {
            try { return Results.Ok(await settings.UpdateAsync(body, ct)); }
            catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["notifications"] = [exception.Message] }); }
        });
        api.MapPost("/renewal-notifications/test", async (RenewalMailNotificationService notifications, CancellationToken ct) =>
        {
            try { await notifications.SendTestAsync(ct); return Results.NoContent(); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { detail = exception.Message }); }
            catch (Exception exception) when (exception is not OperationCanceledException) { return Results.Problem("The test e-mail could not be sent. Check the delivery settings and server logs.", statusCode: 502); }
        });
        
        // Operations
    }

}
