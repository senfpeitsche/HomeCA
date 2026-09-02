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
 static class ConnectorEndpoints
{
    public static void MapConnectorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapGet("/connector-instances", async (ConnectorRegistry connectors, CancellationToken ct) => Results.Ok((await connectors.ListAsync(ct)).Select(c => new { c.Id, c.Name, c.Type, c.CreatedAt })));
        api.MapPost("/connector-instances", async (CreateConnectorRequest request, ConnectorRegistry connectors, CancellationToken ct) =>
        {
            try { return Results.Ok(await connectors.AddAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connector"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPut("/connector-instances/{id}", async (string id, CreateConnectorRequest request, ConnectorRegistry connectors, CancellationToken ct) =>
        {
            try { var connector = await connectors.UpdateAsync(id, request, ct); return connector is null ? Results.NotFound() : Results.Ok(connector); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connector"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPost("/connector-instances/{id}/check", async (string id, ConnectorRegistry registry, ConnectorCatalog catalog, ILogger<global::Program> logger, CancellationToken ct) =>
        {
            var connector = await registry.GetAsync(id, ct);
            var implementation = connector is null ? null : catalog.Find(connector.Type);
            if (connector is null || implementation is null) return Results.NotFound();
            try { return Results.Ok(await implementation.CheckAsync(new ConnectorSettings(connector.Name, connector.Type, connector.Secrets), ct)); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Connector check failed for {ConnectorId} ({Type})", id, connector.Type);
                return Results.Ok(new ConnectorCheckResult(false, [], "The connector could not be reached. Check its settings and network access."));
            }
        });
        api.MapPost("/connector-instances/{id}/txt-test", async (string id, HttpRequest request, ConnectorRegistry registry, ConnectorCatalog catalog, CancellationToken ct) =>
        {
            var connector = await registry.GetAsync(id, ct);
            var implementation = connector is null ? null : catalog.Find(connector.Type);
            if (connector is null || implementation is null) return Results.NotFound();
            var domain = request.Query["domain"].ToString();
            if (string.IsNullOrWhiteSpace(domain)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = ["A domain query parameter is required."] });
            var name = $"_homeca-test.{domain}".TrimEnd('.');
            var value = Guid.NewGuid().ToString("N");
            var settings = new ConnectorSettings(connector.Name, connector.Type, connector.Secrets);
            await implementation.UpsertTxtRecordAsync(settings, name, value, ct);
            await implementation.DeleteTxtRecordAsync(settings, name, value, ct);
            return Results.NoContent();
        });
        api.MapDelete("/connector-instances/{id}", async (string id, ConnectorRegistry connectors, CancellationToken ct) => await connectors.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        
        // ACME (authenticated)
    }

}
