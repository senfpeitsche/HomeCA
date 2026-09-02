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
 static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapGet("/audit", async (HttpRequest request, LocalAdministrationService administration, CancellationToken ct) =>
        {
            var skip = int.TryParse(request.Query["skip"], out var s) ? s : 0;
            var take = int.TryParse(request.Query["take"], out var t) ? t : 100;
            var action = request.Query["action"].FirstOrDefault();
            return Results.Ok(await administration.ReadAuditLogAsync(skip, take, action, ct));
        });
        
        // ── Blazor UI ───────────────────────────────────────────────────────────────
    }

}
