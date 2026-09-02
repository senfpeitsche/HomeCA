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
 static class AuthorityEndpoints
{
    public static void MapAuthorityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
        api.MapPost("/authorities/initialize", async (CertificateAuthorityService authorities, SetupStateService setupState, CancellationToken ct) =>
        {
            var result = await authorities.InitializeAsync(ct);
            // CA eingerichtet → Wizard ist fertig (TLS ist optional, kann über Einstellungen nachgeholt werden)
            await setupState.AdvanceAsync(SetupPhase.PasswordChanged, ct);
            await setupState.SkipWizardAsync(ct);
            return Results.Ok(result);
        });
        api.MapGet("/authorities", async (CertificateAuthorityService authorities, CancellationToken ct) => Results.Ok(await authorities.ListAsync(ct)));
        api.MapPost("/authorities", async (CreateAuthorityRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            try { return Results.Ok(await authorities.CreateAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["authority"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPost("/authorities/rotate-intermediate", async (RotateIntermediateRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            try { return Results.Ok(await authorities.RotateIntermediateAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["authority"] = [ex.Message] }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPut("/authorities/{id}", async (string id, UpdateAuthorityRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            try { var authority = await authorities.UpdateAsync(id, request, ct); return authority is null ? Results.NotFound() : Results.Ok(authority); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapPost("/authorities/{id}/revoke", async (string id, CertificateAuthorityService authorities, CrlService crl, CancellationToken ct) =>
        {
            try
            {
                var authority = await authorities.RevokeAsync(id, ct);
                if (authority is null) return Results.NotFound();
                if (authority.Type == "intermediate" && authority.ParentId is not null)
                    await crl.GenerateAsync(authority.ParentId, ct);
                return Results.Ok(authority);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapDelete("/authorities/{id}", async (string id, CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            try { return await authorities.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        });
        api.MapGet("/authorities/{id}/certificate", async (string id, string format, CertificateAuthorityService authorities, CancellationToken ct) =>
        {
            try { var export = await authorities.ExportCertificateAsync(id, format, ct); return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [ex.Message] }); }
        });
        
        // Certificates
    }

}
