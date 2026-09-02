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
 static class DomainEndpoints
{
    public static void MapDomainEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapGet("/domains", async (DomainRegistry domains, CancellationToken ct) => Results.Ok(await domains.ListAsync(ct)));
        api.MapPost("/domains", async (CreateDomainRequest request, DomainRegistry domains, CancellationToken ct) => Results.Ok(await domains.AddAsync(request, ct)));
        api.MapPut("/domains/{name}", async (string name, CreateDomainRequest request, DomainRegistry domains, CancellationToken ct) =>
        {
            try { var domain = await domains.UpdateAsync(name, request, ct); return domain is null ? Results.NotFound() : Results.Ok(domain); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapDelete("/domains/{name}", async (string name, DomainRegistry domains, CancellationToken ct) => await domains.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound());
        
        // Profiles
        api.MapGet("/profiles", async (TargetProfileRegistry profiles, CancellationToken ct) => Results.Ok(await profiles.ListAsync(ct)));
        api.MapPost("/profiles", async (CreateTargetProfileRequest request, TargetProfileRegistry profiles, CancellationToken ct) =>
        {
            try { return Results.Ok(await profiles.AddAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPut("/profiles/{id}", async (string id, UpdateTargetProfileRequest request, TargetProfileRegistry profiles, CancellationToken ct) =>
        {
            try { var profile = await profiles.UpdateAsync(id, request, ct); return profile is null ? Results.NotFound() : Results.Ok(profile); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [ex.Message] }); }
        });
        api.MapDelete("/profiles/{id}", async (string id, TargetProfileRegistry profiles, CancellationToken ct) =>
        {
            try { return await profiles.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        
        // Connectors
    }

}
